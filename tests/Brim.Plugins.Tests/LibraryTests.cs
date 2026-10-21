using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Brim.Core;
using Brim.Plugins;
using Brim.Sdk;
using Microsoft.Extensions.Time.Testing;

namespace Brim.Plugins.Tests;

/// <summary>Antwortet auf feste URLs; alles andere ist 404.</summary>
internal sealed class FakeWeb : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var response = Files.TryGetValue(request.RequestUri!.ToString(), out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        return Task.FromResult(response);
    }
}

public sealed class CatalogTests
{
    private const string Repo = "https://github.com/someone/brim-weather";

    private static string Entry(string id = "de.someone.weather", string download = Repo + "/releases/download/v1.0.0/weather.zip", string sha = null!) =>
        $$"""{ "id": "{{id}}", "name": "Wetter", "repository": "{{Repo}}", "version": "1.0.0", "download": "{{download}}", "sha256": "{{sha ?? new string('a', 64)}}", "permissions": ["network"] }""";

    [Fact]
    public void Broken_entries_are_skipped_with_a_reason()
    {
        var json = $$"""{ "schema": 1, "plugins": [ {{Entry()}}, {{Entry("de.someone.other", "https://example.com/x.zip")}}, {{Entry("de.someone.third", sha: "ABC")}}, {{Entry()}} ] }""";

        var catalog = PluginCatalog.Parse(json, out var skipped);

        var entry = Assert.Single(catalog.Plugins);
        Assert.Equal("de.someone.weather", entry.Id);
        Assert.False(entry.Official);
        Assert.Equal(3, skipped.Count);
        Assert.Contains(skipped, s => s.Contains("Releases von"));
        Assert.Contains(skipped, s => s.Contains("sha256"));
        Assert.Contains(skipped, s => s.Contains("doppelt"));
    }

    [Fact]
    public void Unknown_schema_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => PluginCatalog.Parse("""{ "schema": 2, "plugins": [] }""", out _));
    }

    [Theory]
    [InlineData("1.3.0", "1.2.9", true)]
    [InlineData("1.3", "1.3.0", false)]
    [InlineData("v2.0.0", "1.9", true)]
    [InlineData("1.0.0-beta", "1.0.0", false)]
    [InlineData("kaputt", "1.0.0", false)]
    [InlineData("1.0.0", "kaputt", true)]
    public void Versions_compare_numerically(string candidate, string installed, bool newer)
    {
        Assert.Equal(newer, PluginVersion.IsNewer(candidate, installed));
    }

    /// <summary>Der echte Katalog im Repository muss sich fehlerfrei lesen lassen.</summary>
    [Fact]
    public void Shipped_catalog_is_valid()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "catalog", "plugins.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var catalog = PluginCatalog.Parse(File.ReadAllText(Path.Combine(dir.FullName, "catalog", "plugins.json")), out var skipped);
        Assert.Empty(skipped);
        Assert.NotEmpty(catalog.Plugins);
        Assert.All(catalog.Plugins, p => Assert.True(p.Official));
    }
}

[Collection(PluginRunnerCollection.Name)]
public sealed class LibraryTests : IAsyncDisposable
{
    private const string PluginId = "de.test.weather";
    private const string Repo = "https://github.com/wheatley2056/brim-weather";
    private const string CatalogUrl = "https://example.test/plugins.json";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "brim-library-" + Guid.NewGuid().ToString("N"));
    private readonly string _devFolder;
    private readonly FakeWeb _web = new();
    private readonly BrimSettings _settings = new() { Library = new PluginLibrarySettings { CatalogUrl = CatalogUrl } };
    private readonly ActivityHub _hub = new(new FakeTimeProvider());
    private readonly InProcessLauncher _launcher;
    private int _connected;
    private PluginHostModule? _host;
    private PluginLibrary? _library;

    public LibraryTests()
    {
        _devFolder = Path.Combine(_data, "dev");
        PluginRunner.ConnectTimeout = TimeSpan.FromSeconds(5);
        _launcher = new InProcessLauncher(async (pipe, token) =>
        {
            await using var brim = await BrimPlugin.ConnectAsync(pipe, token);
            Interlocked.Increment(ref _connected);
            await brim.RunAsync(token);
        });
    }

    private string Plugins => Path.Combine(_data, "Plugins");

    private static byte[] Zip(string version, string permissions = "[]", string? id = null, string prefix = "", string? evilName = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }
            Add(prefix + "plugin.json", $$"""{ "id": "{{id ?? PluginId}}", "name": "Wetter", "version": "{{version}}", "entry": "weather", "permissions": {{permissions}}, "settings": [{ "key": "city", "default": "Freiburg" }] }""");
            Add(prefix + "readme.txt", version);
            if (evilName is not null)
            {
                Add(evilName, "böse");
            }
        }
        return stream.ToArray();
    }

    /// <summary>Stellt ein Zip unter einer Release-URL bereit und trägt es in den Katalog ein.</summary>
    private void Publish(string version, byte[] zip, string permissions = "[]", string? sha = null)
    {
        var url = $"{Repo}/releases/download/v{version}/weather.zip";
        _web.Files[url] = zip;
        sha ??= Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        _web.Files[CatalogUrl] = Encoding.UTF8.GetBytes($$"""
            { "schema": 1, "plugins": [ { "id": "{{PluginId}}", "name": "Wetter", "summary": "Wetter in der Notch", "repository": "{{Repo}}",
              "version": "{{version}}", "download": "{{url}}", "sha256": "{{sha}}", "size": {{zip.Length}}, "permissions": {{permissions}} } ] }
            """);
    }

    private async Task<PluginLibrary> Start(params string[] extraFolders)
    {
        _host = new PluginHostModule([Plugins, .. extraFolders], _launcher, _ => { });
        await _host.StartAsync(new ModuleContext(PluginHostModule.ModuleId, _hub, _settings), CancellationToken.None);
        _library = new PluginLibrary(new HttpClient(_web), _host, () => _settings, _data, () => "2.3.0");
        await _library.RefreshAsync();
        return _library;
    }

    private LibraryItem Item() => _library!.Items().Single(i => i.Id == PluginId);

    private static async Task Until(Func<bool> condition)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > 5000)
            {
                throw new TimeoutException("Bedingung nicht erreicht");
            }
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Installing_from_the_catalog_grants_permissions_and_starts_the_plugin()
    {
        Publish("1.0.0", Zip("1.0.0", """["network"]"""), """["network"]""");
        var library = await Start();
        Assert.Equal(LibraryStatus.Available, Item().Status);

        Assert.True(await library.InstallAsync(PluginId));

        Assert.True(File.Exists(Path.Combine(Plugins, PluginId, "plugin.json")));
        Assert.Equal(LibraryStatus.Installed, Item().Status);
        Assert.Equal(["network"], _settings.Plugins[PluginId].Granted);
        await Until(() => _connected == 1 && _host!.StateOf(PluginId) == PluginState.Running);
        Assert.False(Directory.Exists(Path.Combine(_data, "PluginStaging")) && Directory.EnumerateFileSystemEntries(Path.Combine(_data, "PluginStaging")).Any());
    }

    [Fact]
    public async Task A_zip_in_a_subfolder_is_found()
    {
        Publish("1.0.0", Zip("1.0.0", prefix: "weather/"));
        var library = await Start();

        Assert.True(await library.InstallAsync(PluginId));
        Assert.True(File.Exists(Path.Combine(Plugins, PluginId, "readme.txt")));
    }

    [Fact]
    public async Task A_wrong_checksum_installs_nothing()
    {
        Publish("1.0.0", Zip("1.0.0"), sha: new string('0', 64));
        var library = await Start();

        Assert.False(await library.InstallAsync(PluginId));

        Assert.Contains("Prüfsumme", library.Error(PluginId));
        Assert.False(Directory.Exists(Path.Combine(Plugins, PluginId)));
        Assert.Equal(LibraryStatus.Available, Item().Status);
    }

    [Fact]
    public async Task Zip_must_contain_the_promised_plugin()
    {
        Publish("1.0.0", Zip("1.0.0", """["alerts"]"""));
        var library = await Start();

        Assert.False(await library.InstallAsync(PluginId));
        Assert.Contains("alerts", library.Error(PluginId));

        Publish("1.0.0", Zip("1.0.0", id: "de.test.other"));
        await library.RefreshAsync();
        Assert.False(await library.InstallAsync(PluginId));
        Assert.Contains("de.test.other", library.Error(PluginId));
    }

    [Fact]
    public async Task Paths_leaving_the_folder_are_refused()
    {
        Publish("1.0.0", Zip("1.0.0", evilName: "../../evil.txt"));
        var library = await Start();

        Assert.False(await library.InstallAsync(PluginId));
        Assert.Contains("ungültigen Pfad", library.Error(PluginId));
        Assert.False(File.Exists(Path.Combine(_data, "evil.txt")));
    }

    [Fact]
    public async Task Updates_keep_settings_and_switch_but_new_permissions_need_consent()
    {
        Publish("1.0.0", Zip("1.0.0"));
        var library = await Start();
        Assert.True(await library.InstallAsync(PluginId));
        _settings.Plugins[PluginId].Values["city"] = System.Text.Json.JsonSerializer.SerializeToElement("Bad Krozingen");
        await Until(() => _connected == 1);

        Publish("1.1.0", Zip("1.1.0"));
        await library.RefreshAsync();
        Assert.Equal(LibraryStatus.UpdateAvailable, Item().Status);
        await library.UpdateAllAsync();

        Assert.Equal("1.1.0", Item().Installed!.Version);
        Assert.Equal(LibraryStatus.Installed, Item().Status);
        Assert.Equal("Bad Krozingen", _settings.Plugins[PluginId].Values["city"].GetString());
        await Until(() => _connected == 2 && _host!.StateOf(PluginId) == PluginState.Running);

        Publish("1.2.0", Zip("1.2.0", """["network"]"""), """["network"]""");
        await library.RefreshAsync();
        Assert.Equal(LibraryStatus.UpdateNeedsConsent, Item().Status);
        Assert.Equal(["network"], Item().NewPermissions);
        await library.UpdateAllAsync();
        Assert.Equal("1.1.0", Item().Installed!.Version);

        // Mit Zustimmung (Knopf „Aktualisieren“).
        Assert.True(await library.InstallAsync(PluginId));
        Assert.Equal("1.2.0", Item().Installed!.Version);
        Assert.Equal(["network"], _settings.Plugins[PluginId].Granted);
    }

    [Fact]
    public async Task Removing_deletes_the_folder_and_forgets_the_plugin()
    {
        Publish("1.0.0", Zip("1.0.0"));
        var library = await Start();
        Assert.True(await library.InstallAsync(PluginId));

        Assert.True(await library.RemoveAsync(PluginId));

        Assert.False(Directory.Exists(Path.Combine(Plugins, PluginId)));
        Assert.Empty(Directory.EnumerateDirectories(Plugins));
        Assert.False(_settings.Plugins.ContainsKey(PluginId));
        Assert.Equal(LibraryStatus.Available, Item().Status);
    }

    [Fact]
    public async Task Plugins_in_own_folders_are_left_alone()
    {
        var dev = Path.Combine(_devFolder, "weather");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "plugin.json"), $$"""{ "id": "{{PluginId}}", "name": "Wetter", "version": "0.1.0", "entry": "weather" }""");
        Publish("1.0.0", Zip("1.0.0"));
        var library = await Start(_devFolder);

        Assert.Equal(LibraryStatus.Local, Item().Status);
        await library.UpdateAllAsync();
        Assert.False(await library.InstallAsync(PluginId));
        Assert.False(await library.RemoveAsync(PluginId));
        Assert.True(File.Exists(Path.Combine(dev, "plugin.json")));
    }

    [Fact]
    public async Task Without_network_the_cached_catalog_is_used()
    {
        Publish("1.0.0", Zip("1.0.0"));
        await Start();
        _web.Files.Clear();
        _library!.Dispose();

        var offline = new PluginLibrary(new HttpClient(_web), _host!, () => _settings, _data, () => null);
        await offline.RefreshAsync();

        Assert.Equal("Katalog nicht erreichbar", offline.CatalogError);
        Assert.Single(offline.Items());
        offline.Dispose();
    }

    [Fact]
    public async Task Plugins_needing_a_newer_brim_are_marked()
    {
        Publish("1.0.0", Zip("1.0.0"));
        var catalog = Encoding.UTF8.GetString(_web.Files[CatalogUrl]).Replace("\"permissions\"", "\"minBrim\": \"3.0.0\", \"permissions\"");
        _web.Files[CatalogUrl] = Encoding.UTF8.GetBytes(catalog);
        await Start();

        Assert.Equal(LibraryStatus.NeedsNewerBrim, Item().Status);
    }

    public async ValueTask DisposeAsync()
    {
        _library?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

namespace Brim.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brim-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsFile => Path.Combine(_root, "settings.json");

    private SettingsStore Store() => new(SettingsFile);

    [Fact]
    public void Missing_or_null_sections_fall_back_to_defaults()
    {
        // Von Hand bearbeitet: ein Abschnitt steht ausdrücklich auf null. Das darf Brim nicht umwerfen.
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsFile,
            """{ "Now": null, "Appearance": null, "AutoHide": null, "Plugins": null, "Notifications": null, "Share": null, "Display": null }""");

        var settings = Store().Load();

        Assert.NotNull(settings.Now);
        Assert.NotEmpty(settings.Now.Tiles);
        Assert.NotNull(settings.Appearance.Fonts.Custom);
        Assert.NotNull(settings.AutoHide.Apps);
        Assert.NotNull(settings.Notifications.MutedApps);
        Assert.Empty(settings.Plugins);
        Assert.False(string.IsNullOrEmpty(settings.Share.MailboxUrl));
        Assert.False(string.IsNullOrEmpty(settings.Share.TransitRelay));
        Assert.NotNull(settings.Display);
        Assert.NotNull(settings.Appearance);
        Assert.NotNull(settings.Animation);
    }

    [Fact]
    public void Settings_survive_save_and_load()
    {
        var store = Store();
        var settings = store.Load();
        settings.Interaction.HotkeyEnabled = false;
        settings.AutoHide.Apps.Add(new AppRule { Process = "obs64", Visibility = AppVisibility.AlwaysShow });
        store.Save(settings);

        var reloaded = Store();
        var loaded = reloaded.Load();

        Assert.True(reloaded.Existed);
        Assert.False(loaded.Interaction.HotkeyEnabled);
        Assert.Equal("obs64", Assert.Single(loaded.AutoHide.Apps).Process);
        Assert.Equal(AppVisibility.AlwaysShow, loaded.AutoHide.Apps[0].Visibility);
    }

    [Fact]
    public void Concurrent_saves_all_succeed()
    {
        // Der Schalter im Tab „Plugins“ speichert von einem Plugin-Thread, die Einstellungsseite vom UI-Thread.
        var store = Store();
        store.Load();
        var failures = 0;

        Parallel.For(0, 20, _ =>
        {
            try
            {
                store.Save(store.Current);
            }
            catch (IOException)
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.Equal(0, failures);
        Assert.Contains("\"Interaction\"", File.ReadAllText(SettingsFile));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class ActivityExpiryTests
{
    [Fact]
    public void There_is_no_expiry_while_nothing_is_timed()
    {
        var hub = new ActivityHub();

        Assert.Null(hub.NextExpiry());

        hub.Post(new Activity { ModuleId = "media", Id = "now" });
        Assert.Null(hub.NextExpiry());

        hub.Post(new Activity { ModuleId = "audio", Id = "hud", Priority = ActivityPriority.Transient });
        Assert.NotNull(hub.NextExpiry());
    }
}

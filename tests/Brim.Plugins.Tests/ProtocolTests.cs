using System.Text.Json;
using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Brim.Plugins;

namespace Brim.Plugins.Tests;

public class ProtocolTests
{
    [Fact]
    public void Widgets_travel_as_json_with_type()
    {
        Widget widget = new StackWidget(
            new IconWidget(Glyphs.Clock) { Color = "#FF8A66" },
            new TextWidget("Hallo") { Style = TextStyle.Mono, Grow = 1 },
            new ButtonWidget("go") { Label = "Los", Kind = ButtonKind.Secondary })
        {
            Padding = (18, 0, 18, 0),
            Background = BrimColors.Graphite,
        };

        var json = JsonSerializer.Serialize(widget, PluginJson.Options);
        var back = (StackWidget)JsonSerializer.Deserialize<Widget>(json, PluginJson.Options)!;

        Assert.Contains("\"type\":\"stack\"", json);
        Assert.Contains("\"padding\":[18,0,18,0]", json);
        Assert.Equal((18d, 0d, 18d, 0d), back.Padding);
        Assert.Equal("Hallo", ((TextWidget)back.Children[1]).Text);
        Assert.Equal(TextStyle.Mono, ((TextWidget)back.Children[1]).Style);
        Assert.Equal(ButtonKind.Secondary, ((ButtonWidget)back.Children[2]).Kind);
    }

    [Fact]
    public void Plugins_can_write_widgets_by_hand()
    {
        var widget = JsonSerializer.Deserialize<Widget>("""
            { "type": "stack", "orientation": "vertical", "padding": 8, "children": [
                { "type": "text", "text": "CPU", "style": "secondary" },
                { "type": "ring", "value": 0.4, "color": "#5BD98A" } ] }
            """, PluginJson.Options);

        var stack = Assert.IsType<StackWidget>(widget);
        Assert.Equal(StackOrientation.Vertical, stack.Orientation);
        Assert.Equal((8d, 8d, 8d, 8d), stack.Padding);
        Assert.Equal(0.4, Assert.IsType<RingWidget>(stack.Children[1]).Value);
        Assert.Null(WidgetValidator.Validate(stack));
    }

    [Theory]
    [InlineData("""{ "type": "text", "text": "x", "color": "rot" }""", "keine Farbe")]
    [InlineData("""{ "type": "text", "text": "x", "fontSize": 9000 }""", "fontSize")]
    [InlineData("""{ "type": "progress", "value": 3 }""", "value")]
    [InlineData("""{ "type": "button", "actionId": "" }""", "actionId")]
    public void Validator_rejects_bad_widgets(string json, string expected)
    {
        var widget = JsonSerializer.Deserialize<Widget>(json, PluginJson.Options)!;

        Assert.Contains(expected, WidgetValidator.Validate(widget));
    }

    [Fact]
    public void Validator_limits_size_and_depth()
    {
        Widget deep = new TextWidget("x");
        for (var i = 0; i < 20; i++)
        {
            deep = new StackWidget(deep);
        }
        var wide = new ListWidget(Enumerable.Range(0, 500).Select(i => (Widget)new TextWidget(i.ToString())).ToList());

        Assert.Contains("tiefer", WidgetValidator.Validate(deep));
        Assert.Contains("Bausteine", WidgetValidator.Validate(wide));
    }

    [Fact]
    public void Actions_are_mapped_through_the_whole_tree()
    {
        var widget = new StackWidget(
            new ButtonWidget("a"),
            new ListWidget([new SwitchWidget("b", true), new ProgressWidget(0.5) { SeekActionId = "c" }]));

        var actions = WidgetTree.All(WidgetTree.MapActions(widget, a => "x|" + a)).Select(w => w switch
        {
            ButtonWidget b => b.ActionId,
            SwitchWidget s => s.ActionId,
            ProgressWidget p => p.SeekActionId,
            _ => null,
        }).OfType<string>();

        Assert.Equal(["x|a", "x|b", "x|c"], actions);
    }
}

public class ManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brim-manifest-" + Guid.NewGuid().ToString("N"));

    public ManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PluginManifest Write(string json)
    {
        File.WriteAllText(Path.Combine(_dir, PluginManifest.FileName), json);
        return PluginManifest.Load(_dir);
    }

    [Fact]
    public void Reads_the_manifest_from_the_plan()
    {
        File.WriteAllText(Path.Combine(_dir, "ServerStatus.exe"), "");

        var manifest = Write("""
            {
              "id": "de.twogg.serverstatus",
              "name": "TwoGG Serverstatus",
              "version": "1.0.0",
              "entry": "ServerStatus.exe",
              "api": "1",
              "slots": ["compact.trailing", "expanded.tab"],
              "permissions": ["network"],
              "settings": [{ "key": "server", "type": "string", "label": "Server-Adresse", "default": "twogg.de" }]
            }
            """);

        Assert.Equal("de.twogg.serverstatus", manifest.Id);
        Assert.Equal(Path.Combine(_dir, "ServerStatus.exe"), manifest.ResolveEntry());
        Assert.Equal("twogg.de", manifest.EffectiveSettings(null)["server"].GetString());
        var custom = new Dictionary<string, JsonElement> { ["server"] = JsonSerializer.SerializeToElement("example.org") };
        Assert.Equal("example.org", manifest.EffectiveSettings(custom)["server"].GetString());
    }

    [Fact]
    public void Commands_on_the_path_are_allowed()
    {
        var manifest = Write("""{ "id": "de.test.py", "name": "Py", "entry": "py", "args": ["plugin.py"] }""");

        Assert.Equal("py", manifest.ResolveEntry());
    }

    [Theory]
    [InlineData("""{ "id": "Kaputt", "name": "x", "entry": "py" }""", "Kennung")]
    [InlineData("""{ "id": "de.test.x", "name": "x", "entry": "../boese.exe" }""", "nicht im Ordner")]
    [InlineData("""{ "id": "de.test.x", "name": "x", "entry": "C:/Windows/notepad.exe" }""", "nicht im Ordner")]
    [InlineData("""{ "id": "de.test.x", "name": "x", "entry": "py", "permissions": ["root"] }""", "Unbekanntes Recht")]
    [InlineData("""{ "id": "de.test.x", "name": "x", "entry": "py", "api": "2" }""", "API-Version")]
    [InlineData("""{ "id": "de.test.x", "name": "x", "entry": """, "kein gültiges JSON")]
    [InlineData("""{ "id": "de.test.x", "name": "x", "entry": "py", "shelfActions": [{ "id": "up", "label": "Hochladen" }] }""", "braucht das Recht „shelf“")]
    [InlineData("""{ "id": "de.test.x", "name": "x", "entry": "py", "notificationApps": ["Teams"] }""", "braucht das Recht „notifications“")]
    public void Rejects_broken_manifests(string json, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => Write(json));

        Assert.Contains(expected, error.Message);
    }
}

public class WaveWidgetTests
{
    [Fact]
    public void Wave_travels_as_json_and_is_validated()
    {
        var widget = JsonSerializer.Deserialize<Widget>("""{ "type": "wave", "values": [0, 0.5, 1], "color": "#5BD98A" }""", PluginJson.Options);

        var wave = Assert.IsType<WaveWidget>(widget);
        Assert.Equal([0, 0.5, 1], wave.Values);
        Assert.Null(WidgetValidator.Validate(wave));
        Assert.Contains("zwischen 0 und 1", WidgetValidator.Validate(wave with { Values = [1.5] }));
    }
}

using Brim.Core.Widgets;
using Brim.Modules;

namespace Brim.Modules.Tests;

public class ChangelogTests
{
    [Fact]
    public void Markdown_becomes_readable_lines()
    {
        var lines = Changelog.ToWidgets("""
            ## Neu
            - **Teilen** per Code mit [Magic Wormhole](https://magic-wormhole.readthedocs.io)
            * Ablage merkt sich `Ordner`

            Kleinere Korrekturen.
            """).Cast<TextWidget>().Select(t => t.Text).ToList();

        Assert.Equal(["Neu", "•  Teilen per Code mit Magic Wormhole", "•  Ablage merkt sich Ordner", "Kleinere Korrekturen."], lines);
    }

    [Fact]
    public void Lines_wrap_instead_of_being_cut()
    {
        Assert.All(Changelog.ToWidgets("- eine sehr lange Zeile"), w => Assert.True(((TextWidget)w).Wrap));
    }
}

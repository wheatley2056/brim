using Brim.Core;

namespace Brim.Core.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void Light_mode_needs_material_and_follows_the_taskbar_mode()
    {
        Assert.True(ThemePalette.IsLight(NotchTheme.System, NotchMaterial.Mica, windowsUsesLight: true));
        Assert.False(ThemePalette.IsLight(NotchTheme.System, NotchMaterial.Mica, windowsUsesLight: false));
        Assert.True(ThemePalette.IsLight(NotchTheme.Light, NotchMaterial.Acrylic, windowsUsesLight: false));
        Assert.False(ThemePalette.IsLight(NotchTheme.Dark, NotchMaterial.Mica, windowsUsesLight: true));
        // Der schwarze Körper bleibt dunkel.
        Assert.False(ThemePalette.IsLight(NotchTheme.Light, NotchMaterial.Black, windowsUsesLight: true));
    }

    [Fact]
    public void Grays_flip_text_dark_and_surfaces_light()
    {
        Assert.Equal("#1A1A1A", ThemePalette.ForLight(BrimColors.Fog));
        Assert.Equal("#FFFFFF", ThemePalette.ForLight(BrimColors.Obsidian));
        Assert.True(ThemePalette.Contrast(ThemePalette.ForLight(BrimColors.Slate), ThemePalette.LightSurface) >= 4.5);
        // Durchscheinend Weiß wird durchscheinend Schwarz.
        var (a, r, g, b) = AccentColor.Parse(ThemePalette.ForLight("#33FFFFFF"));
        Assert.True(a is > 0 and < 0x33);
        Assert.Equal((0, 0, 0), (r, g, b));
    }

    [Theory]
    [InlineData(BrimColors.Charge)]
    [InlineData(BrimColors.Warning)]
    [InlineData(BrimColors.Danger)]
    [InlineData(BrimColors.Cpu)]
    [InlineData(BrimColors.DefaultAccent)]
    public void Colors_stay_readable_on_light_mica(string color)
    {
        var light = ThemePalette.ForLight(color);
        Assert.True(ThemePalette.Contrast(light, ThemePalette.LightSurface) >= 4.5, $"{color} → {light}");
    }

    [Fact]
    public void New_installs_use_mica_follow_windows_and_take_its_accent()
    {
        var appearance = new AppearanceSettings();
        Assert.Equal(NotchMaterial.Mica, appearance.Material);
        Assert.Equal(NotchTheme.System, appearance.Theme);
        Assert.Equal(AccentSource.Windows, appearance.Accent);
        Assert.Equal(NotchTheme.System, new AppearanceSettings { Theme = (NotchTheme)7 }.Clamped().Theme);
    }
}

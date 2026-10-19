using Brim.Core;

namespace Brim.Core.Tests;

public class GeometryAndColorTests
{
    [Fact]
    public void Canvas_fits_expanded_shape_with_beads()
    {
        var hit = NotchGeometry.HitArea(NotchGeometry.Expanded, 0);

        Assert.True(hit.X >= 0);
        Assert.True(hit.X + hit.Width <= NotchGeometry.Canvas.Width);
        Assert.True(NotchGeometry.Expanded.Height <= NotchGeometry.Canvas.Height);

        var withBeads = NotchGeometry.HitArea(NotchGeometry.Compact, NotchStateMachine.MaxBeads);
        Assert.True(withBeads.X + withBeads.Width <= NotchGeometry.Canvas.Width);
    }

    [Fact]
    public void Hit_area_is_centered_without_beads()
    {
        var hit = NotchGeometry.HitArea(NotchGeometry.Idle, 0);

        Assert.Equal(NotchGeometry.Canvas.Width / 2, hit.X + hit.Width / 2, 3);
        Assert.Equal(NotchGeometry.Idle.OuterWidth, hit.Width, 3);
    }

    [Fact]
    public void Bead_sits_right_of_the_ear()
    {
        var bead = Assert.Single(NotchGeometry.Beads(NotchGeometry.Compact, 1));

        // Aus dem Design: Körper 300, Ohr 10, Abstand 6.
        Assert.Equal(150 + 10 + 6, bead.Left);
    }

    [Fact]
    public void Union_covers_both()
    {
        var u = NotchGeometry.Union((10, 0, 20, 5), (0, 0, 15, 30));

        Assert.Equal((0d, 0d, 30d, 30d), u);
    }

    [Fact]
    public void Accent_from_grey_image_is_null()
    {
        var pixels = Enumerable.Repeat(new byte[] { 128, 128, 128, 255 }, 64).SelectMany(p => p).ToArray();

        Assert.Null(AccentColor.FromBgra(pixels));
    }

    [Fact]
    public void Accent_picks_dominant_vivid_color()
    {
        // 48 Pixel kräftiges Orange, 16 Pixel Blau (BGRA).
        var orange = Enumerable.Repeat(new byte[] { 60, 120, 240, 255 }, 48).SelectMany(p => p);
        var blue = Enumerable.Repeat(new byte[] { 230, 80, 40, 255 }, 16).SelectMany(p => p);

        var hex = AccentColor.FromBgra(orange.Concat(blue).ToArray());

        Assert.NotNull(hex);
        var (_, r, g, b) = AccentColor.Parse(hex);
        Assert.True(r > g && g > b, $"erwartet Orange, war {hex}");
    }

    [Fact]
    public void Settings_are_clamped()
    {
        var clamped = new AnimationSettings { ExpandMs = 5000, RetractMs = 1, Damping = 3 }.Clamped();

        Assert.Equal(800, clamped.ExpandMs);
        Assert.Equal(120, clamped.RetractMs);
        Assert.Equal(1.0, clamped.Damping);
    }

    [Fact]
    public void Settings_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "brim-test-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new BrimSettings { Monitors = MonitorMode.All, Animation = { Style = AnimationStyle.Fade } });

            var loaded = new SettingsStore(path).Load();

            Assert.Equal(MonitorMode.All, loaded.Monitors);
            Assert.Equal(AnimationStyle.Fade, loaded.Animation.Style);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

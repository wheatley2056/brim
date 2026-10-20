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

    [Theory]
    [InlineData(NotchForm.Notch)]
    [InlineData(NotchForm.Island)]
    [InlineData(NotchForm.Edge)]
    [InlineData(NotchForm.Tab)]
    public void Every_form_keeps_sizes_and_fits_the_canvas(NotchForm form)
    {
        foreach (var mode in Enum.GetValues<NotchMode>())
        {
            var plain = NotchGeometry.For(mode);
            var styled = NotchGeometry.For(mode, form);
            Assert.Equal(plain.Width, styled.Width);
            Assert.Equal(plain.Height, styled.Height);
            Assert.True(styled.Radius <= plain.Radius);

            var hit = NotchGeometry.HitArea(styled, 0, NotchGeometry.Gap(form));
            Assert.True(hit.Height <= NotchGeometry.Canvas.Height);
        }
    }

    [Fact]
    public void Only_the_notch_has_ears_and_only_the_island_floats()
    {
        Assert.Equal(NotchGeometry.Expanded, NotchGeometry.For(NotchMode.Expanded, NotchForm.Notch));
        Assert.Equal(0, NotchGeometry.For(NotchMode.Compact, NotchForm.Island).Ear);
        Assert.Equal(1, NotchGeometry.For(NotchMode.Compact, NotchForm.Island).Round);
        Assert.Equal(0, NotchGeometry.For(NotchMode.Compact, NotchForm.Edge).Round);
        // Kante und Tab unterscheiden sich in jedem Zustand deutlich.
        foreach (var mode in new[] { NotchMode.Idle, NotchMode.Compact, NotchMode.Expanded })
        {
            Assert.True(NotchGeometry.For(mode, NotchForm.Edge).Radius >= 4 * NotchGeometry.For(mode, NotchForm.Tab).Radius);
        }

        Assert.Equal(NotchGeometry.IslandGap, NotchGeometry.Gap(NotchForm.Island));
        Assert.Equal(0, NotchGeometry.Gap(NotchForm.Tab));
        // Der Abstand zählt zur Eingabefläche, damit der Hover auf dem Weg nach oben nicht abreißt.
        Assert.Equal(34 + NotchGeometry.IslandGap, NotchGeometry.HitArea(NotchGeometry.Compact, 0, NotchGeometry.IslandGap).Height);
    }

    [Fact]
    public void Outline_matches_the_drawn_body()
    {
        // Notch: Rechteck reicht um den Radius über die Kante, Ohren mit ausgesparten Kreisen.
        var notch = NotchGeometry.Outline(NotchGeometry.Compact, 0, 0);
        var center = NotchGeometry.Canvas.Width / 2;
        Assert.Equal(new RoundRect(center - 150, -17, 300, 51, 17), notch.Body);
        Assert.Equal(2, notch.Ears.Count);
        Assert.Equal(new RoundRect(center - 160, 0, 10, 10, 0), notch.Ears[0]);
        Assert.Equal(new RoundRect(center - 170, 0, 20, 20, 10), notch.EarCutouts[0]);

        // Insel: ganz sichtbar, unter dem Abstand, Radius höchstens halbe Höhe, runde Perlen.
        var island = NotchGeometry.Outline(NotchGeometry.For(NotchMode.Compact, NotchForm.Island), 1, NotchGeometry.IslandGap);
        Assert.Equal(new RoundRect(center - 150, NotchGeometry.IslandGap, 300, 34, 17), island.Body);
        Assert.Empty(island.Ears);
        var bead = Assert.Single(island.Beads);
        Assert.Equal(NotchGeometry.IslandGap, bead.Y);
        Assert.Equal(NotchGeometry.BeadSize, bead.Height);
        Assert.Equal(center + 150 + NotchGeometry.BeadGap, bead.X);
    }

    [Fact]
    public void Appearance_is_clamped()
    {
        Assert.Equal(1.3, new AppearanceSettings { Size = 3 }.Clamped().Size);
        Assert.Equal(1.0, new AppearanceSettings { Size = double.NaN }.Clamped().Size);
        Assert.Equal(NotchForm.Notch, new AppearanceSettings { Form = (NotchForm)42 }.Clamped().Form);
        Assert.Equal(NotchMaterial.Black, new AppearanceSettings { Material = (NotchMaterial)9 }.Clamped().Material);
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

using Brim.Core;

namespace Brim.Core.Tests;

public class AutoHideTests
{
    private static SceneWindow Window(string process, bool inZone = true, bool fullscreen = false, bool maximized = false, bool foreground = false) =>
        new(process, "Class") { InZone = inZone, Fullscreen = fullscreen, Maximized = maximized, Foreground = foreground };

    private static AutoHideReason Evaluate(AutoHideSettings settings, params SceneWindow[] windows) =>
        AutoHideRules.Evaluate(settings, new AutoHideScene { Windows = windows });

    [Fact]
    public void Free_desktop_keeps_the_notch()
    {
        Assert.Equal(AutoHideReason.None, Evaluate(new AutoHideSettings()));
        Assert.Equal(AutoHideReason.None, Evaluate(new AutoHideSettings(), Window("notepad", inZone: false, foreground: true)));
    }

    [Fact]
    public void Each_window_kind_has_its_own_switch()
    {
        var settings = new AutoHideSettings { WhenOverlapped = false };

        Assert.Equal(AutoHideReason.Fullscreen, Evaluate(settings, Window("vlc", fullscreen: true, maximized: true)));
        Assert.Equal(AutoHideReason.Maximized, Evaluate(settings, Window("code", maximized: true)));
        Assert.Equal(AutoHideReason.None, Evaluate(settings, Window("notepad")));

        settings.WhenFullscreen = false;
        Assert.Equal(AutoHideReason.None, Evaluate(settings, Window("vlc", fullscreen: true)));
    }

    [Fact]
    public void Plain_window_in_the_zone_hides_by_default()
    {
        Assert.Equal(AutoHideReason.Overlap, Evaluate(new AutoHideSettings(), Window("notepad")));
    }

    [Fact]
    public void Desktop_only_hides_for_any_foreground_window()
    {
        var settings = new AutoHideSettings { WhenAnyForeground = true };

        Assert.Equal(AutoHideReason.Foreground, Evaluate(settings, Window("notepad", inZone: false, foreground: true)));
    }

    [Fact]
    public void Always_show_app_wins_over_window_rules_and_its_windows_never_cover()
    {
        var settings = new AutoHideSettings { Apps = [new AppRule { Process = "OBS64.exe", Visibility = AppVisibility.AlwaysShow }] };

        Assert.Equal(AutoHideReason.None, Evaluate(settings, Window("obs64", maximized: true, foreground: true), Window("chrome", fullscreen: true)));
        Assert.Equal(AutoHideReason.None, Evaluate(settings, Window("obs64", fullscreen: true)));
        Assert.Equal(AutoHideReason.Fullscreen, Evaluate(settings, Window("obs64", fullscreen: true), Window("vlc", fullscreen: true)));
    }

    [Fact]
    public void Always_hide_app_hides_while_in_front()
    {
        var settings = new AutoHideSettings { Apps = [new AppRule { Process = "Premiere", Visibility = AppVisibility.AlwaysHide }] };

        Assert.Equal(AutoHideReason.App, Evaluate(settings, Window("premiere", inZone: false, foreground: true)));
        Assert.Equal(AutoHideReason.None, Evaluate(settings, Window("premiere", inZone: false)));
    }

    [Fact]
    public void Dragging_and_screen_sharing_go_first()
    {
        var settings = new AutoHideSettings { Apps = [new AppRule { Process = "obs64", Visibility = AppVisibility.AlwaysShow }] };
        var obs = Window("obs64", foreground: true);

        Assert.Equal(AutoHideReason.ScreenSharing, AutoHideRules.Evaluate(settings, new AutoHideScene { Windows = [obs], ScreenSharing = true }));
        Assert.Equal(AutoHideReason.Dragging, AutoHideRules.Evaluate(settings, new AutoHideScene { Windows = [obs], ScreenSharing = true, DraggingNearTop = true }));

        settings.WhenScreenSharing = false;
        Assert.Equal(AutoHideReason.None, AutoHideRules.Evaluate(settings, new AutoHideScene { Windows = [obs], ScreenSharing = true }));
    }

    [Fact]
    public void Idle_only_when_switched_on_and_reached()
    {
        var scene = new AutoHideScene { Idle = TimeSpan.FromSeconds(30) };

        Assert.Equal(AutoHideReason.None, AutoHideRules.Evaluate(new AutoHideSettings(), scene));
        Assert.Equal(AutoHideReason.Idle, AutoHideRules.Evaluate(new AutoHideSettings { IdleSeconds = 30 }, scene));
        Assert.Equal(AutoHideReason.None, AutoHideRules.Evaluate(new AutoHideSettings { IdleSeconds = 31 }, scene));
    }

    [Theory]
    [InlineData("ZPToolBarParentWndClass", "", true)]
    [InlineData("Chrome_WidgetWin_1", "meet.google.com is sharing your screen.", true)]
    [InlineData("Chrome_WidgetWin_1", "meet.google.com gibt Ihren Bildschirm frei.", true)]
    [InlineData("TeamsWebView", "Sharing control bar | Microsoft Teams", true)]
    [InlineData("Chrome_WidgetWin_1", "Bildschirm teilen – Anleitung – Google Chrome", false)]
    [InlineData("ZPContentViewWndClass", "Zoom Meeting", false)]
    public void Screen_share_markers(string windowClass, string title, bool expected)
    {
        Assert.Equal(expected, ScreenShare.IsSharing([(windowClass, title)], ScreenShare.DefaultMarkers));
    }

    [Fact]
    public void App_rules_survive_saving()
    {
        var path = Path.Combine(Path.GetTempPath(), "brim-autohide-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new BrimSettings();
            settings.AutoHide.Apps.Add(new AppRule { Process = "obs64", Visibility = AppVisibility.AlwaysShow });
            store.Save(settings);

            var loaded = new SettingsStore(path).Load();

            var rule = Assert.Single(loaded.AutoHide.Apps);
            Assert.Equal(AppVisibility.AlwaysShow, rule.Visibility);
            Assert.Equal(ScreenShare.DefaultMarkers, loaded.AutoHide.ScreenShareMarkers);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

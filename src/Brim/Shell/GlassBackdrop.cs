using Brim.Core;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Brim.Shell;

/// <summary>
/// Glas (Einstellungen → Aussehen): Acryl hinter dem Fenster, also der weichgezeichnete Desktop. Die Region des
/// Fensters beschneidet es auf den Umriss der Notch. Anders als <c>DesktopAcrylicBackdrop</c> bleibt es auch an,
/// wenn das Fenster nicht aktiv ist – das ist die Notch nie.
/// </summary>
internal sealed class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported())
        {
            Log.Info("Glas wird von diesem System nicht unterstützt");
            return;
        }
        _configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = Colors.Black,
            TintOpacity = 0.25f,
            LuminosityOpacity = 0.55f,
            FallbackColor = Color.FromArgb(255, 18, 18, 20),
        };
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }
}

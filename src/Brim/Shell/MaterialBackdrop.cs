using Brim.Core;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Brim.Shell;

/// <summary>
/// Windows-Material hinter der Notch (Einstellungen → Aussehen): Acryl, Mica oder Mica Alt. Die Region des
/// Fensters beschneidet es auf den Umriss der Notch. Anders als <c>DesktopAcrylicBackdrop</c> und
/// <c>MicaBackdrop</c> bleibt es auch an, wenn das Fenster nicht aktiv ist – das ist die Notch nie.
/// </summary>
internal sealed class MaterialBackdrop(NotchMaterial material) : SystemBackdrop
{
    private ISystemBackdropControllerWithTargets? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _controller = Create();
        if (_controller is null)
        {
            Log.Info($"Material {material} wird von diesem System nicht unterstützt");
            return;
        }
        _configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    private ISystemBackdropControllerWithTargets? Create()
    {
        var fallback = Color.FromArgb(255, 18, 18, 20);
        if (material == NotchMaterial.Acrylic)
        {
            return DesktopAcrylicController.IsSupported()
                ? new DesktopAcrylicController
                {
                    Kind = DesktopAcrylicKind.Thin,
                    TintColor = Colors.Black,
                    TintOpacity = 0.25f,
                    LuminosityOpacity = 0.55f,
                    FallbackColor = fallback,
                }
                : null;
        }
        return MicaController.IsSupported()
            ? new MicaController
            {
                Kind = material == NotchMaterial.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base,
                FallbackColor = fallback,
            }
            : null;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        (_controller as IDisposable)?.Dispose();
        _controller = null;
    }
}

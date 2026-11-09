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
internal sealed class MaterialBackdrop(NotchMaterial material, bool light = false) : SystemBackdrop
{
    private ISystemBackdropControllerWithTargets? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _controller = CreateController(material, light);
        if (_controller is null)
        {
            Log.Info($"Material {material} wird von diesem System nicht unterstützt");
            return;
        }
        // Erst die Konfiguration, dann das Ziel (wie in Microsofts Beispiel): Sonst zeigt Windows das helle,
        // inaktive Material – ein flaches Grau.
        _configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark };
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    /// <summary>Controller für das Material, oder null, wenn das System es nicht kann.</summary>
    /// <param name="light">Hell wie Taskleiste und Startmenü im hellen Windows-Modus.</param>
    public static ISystemBackdropControllerWithTargets? CreateController(NotchMaterial kind, bool light = false)
    {
        if (light)
        {
            return CreateLightController(kind);
        }
        var fallback = Color.FromArgb(255, 18, 18, 20);
        if (kind == NotchMaterial.Acrylic)
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
        // Die dunklen Werte von Windows 11 ausdrücklich, damit es nie hell wird, egal was das System meldet.
        var alt = kind == NotchMaterial.MicaAlt;
        return MicaController.IsSupported()
            ? new MicaController
            {
                Kind = alt ? MicaKind.BaseAlt : MicaKind.Base,
                TintColor = alt ? Color.FromArgb(255, 10, 10, 10) : Color.FromArgb(255, 32, 32, 32),
                TintOpacity = alt ? 0f : 0.8f,
                LuminosityOpacity = 1f,
                FallbackColor = fallback,
            }
            : null;
    }

    /// <summary>Die hellen Werte von Windows 11 (wie Startmenü und Flyouts im hellen Modus).</summary>
    private static ISystemBackdropControllerWithTargets? CreateLightController(NotchMaterial kind)
    {
        var fallback = Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
        if (kind == NotchMaterial.Acrylic)
        {
            return DesktopAcrylicController.IsSupported()
                ? new DesktopAcrylicController
                {
                    Kind = DesktopAcrylicKind.Thin,
                    TintColor = Color.FromArgb(255, 0xFC, 0xFC, 0xFC),
                    TintOpacity = 0f,
                    LuminosityOpacity = 0.85f,
                    FallbackColor = fallback,
                }
                : null;
        }
        var alt = kind == NotchMaterial.MicaAlt;
        return MicaController.IsSupported()
            ? new MicaController
            {
                Kind = alt ? MicaKind.BaseAlt : MicaKind.Base,
                TintColor = alt ? Color.FromArgb(255, 0xDA, 0xDA, 0xDA) : Color.FromArgb(255, 0xF3, 0xF3, 0xF3),
                TintOpacity = 0.5f,
                LuminosityOpacity = 1f,
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

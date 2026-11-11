using Brim.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Brim.Shell;

/// <summary>
/// Was Windows über sein Aussehen sagt: Windows-Modus hell/dunkel (wie die Taskleiste), Akzentfarbe und welche
/// Materialien das System kann. <see cref="Changed"/> meldet jede Änderung in den Windows-Einstellungen.
/// </summary>
internal static class WindowsLook
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly UISettings Settings = new();
    private static bool _listening;
    private static event Action? ChangedCore;

    /// <summary>Modus oder Akzentfarbe hat sich geändert (Hintergrund-Thread; selbst auf den UI-Thread wechseln).</summary>
    public static event Action? Changed
    {
        add
        {
            if (!_listening)
            {
                _listening = true;
                // ColorValuesChanged kommt bei Akzentfarbe und beim Wechsel zwischen hell und dunkel.
                Settings.ColorValuesChanged += (_, _) => ChangedCore?.Invoke();
                Settings.AdvancedEffectsEnabledChanged += (_, _) => ChangedCore?.Invoke();
            }
            ChangedCore += value;
        }
        remove => ChangedCore -= value;
    }

    /// <summary>
    /// Windows-Modus hell? Das ist der Modus von Taskleiste und Startmenü (<c>SystemUsesLightTheme</c>), nicht der
    /// der Apps. Brim gehört zur Shell, also zählt dieser.
    /// </summary>
    public static bool SystemUsesLight
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Windows-Akzentfarbe, für den Grund passend abgestuft: auf dunklem Grund die helle Stufe (wie Schalter in
    /// dunklen Windows-Flyouts), auf hellem die dunkle.
    /// </summary>
    public static string Accent(bool light)
    {
        try
        {
            var c = Settings.GetColorValue(light ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch (Exception ex)
        {
            Log.Warn("Akzentfarbe von Windows nicht lesbar", ex);
            return light ? "#005FB8" : "#60CDFF";
        }
    }

    /// <summary>
    /// Material, das wirklich gezeichnet wird: Kann das System das gewählte nicht (Mica gibt es erst ab
    /// Windows 11), nimmt Brim Schwarz statt einer fast durchsichtigen Fläche.
    /// </summary>
    public static NotchMaterial Effective(NotchMaterial material) => material switch
    {
        NotchMaterial.Mica or NotchMaterial.MicaAlt when !Supported(MicaController.IsSupported) => NotchMaterial.Black,
        NotchMaterial.Acrylic when !Supported(DesktopAcrylicController.IsSupported) => NotchMaterial.Black,
        _ => material,
    };

    private static bool Supported(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            Log.Warn("Material: Unterstützung nicht prüfbar", ex);
            return false;
        }
    }
}

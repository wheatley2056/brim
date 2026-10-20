namespace Brim.Modules.Sensors;

/// <summary>Ob Brim mit Paketidentität läuft (Identitätspaket registriert, siehe build/identity).</summary>
public static class Identity
{
    public static bool Present
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current?.Id is not null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}

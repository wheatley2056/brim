using System.Runtime.InteropServices;

namespace Brim.Modules.Audio;

/// <summary>
/// Standard-Ausgabegerät setzen. Windows bietet dafür keine offizielle Schnittstelle; diese
/// undokumentierte, seit Windows 7 unveränderte COM-Schnittstelle nutzen auch EarTrumpet und
/// SoundSwitch. Bewusste Ausnahme von Anforderung A1 (siehe plan.md, Risiken).
/// </summary>
internal static class PolicyConfig
{
    private enum Role
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    // Reihenfolge der Methoden entspricht der Vtable; nur SetDefaultEndpoint wird benutzt.
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(nint deviceId, nint format);
        [PreserveSig] int GetDeviceFormat(nint deviceId, int defaultFormat, nint format);
        [PreserveSig] int ResetDeviceFormat(nint deviceId);
        [PreserveSig] int SetDeviceFormat(nint deviceId, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(nint deviceId, int defaultPeriod, nint defaultPeriodOut, nint minPeriod);
        [PreserveSig] int SetProcessingPeriod(nint deviceId, nint period);
        [PreserveSig] int GetShareMode(nint deviceId, nint mode);
        [PreserveSig] int SetShareMode(nint deviceId, nint mode);
        [PreserveSig] int GetPropertyValue(nint deviceId, nint key, nint value);
        [PreserveSig] int SetPropertyValue(nint deviceId, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    /// <summary>Setzt das Gerät für alle Rollen (Konsole, Multimedia, Kommunikation), wie die Windows-Schnellauswahl.</summary>
    public static void SetDefault(string deviceId)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
            }
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Brim.Modules.Sensors;

public enum Capability { Camera, Microphone }

/// <summary>Eine App, die Kamera oder Mikrofon gerade benutzt.</summary>
public sealed record CapabilityUse(Capability Capability, string App);

public interface ICapabilitySource : IDisposable
{
    /// <summary>Etwas hat sich geändert; von einem beliebigen Thread, oft mehrfach kurz hintereinander.</summary>
    event Action? Changed;

    IReadOnlyList<CapabilityUse> Read();
}

/// <summary>
/// Liest, welche Apps Kamera und Mikrofon benutzen – dieselbe Quelle wie das Mikrofonsymbol der
/// Taskleiste: <c>CapabilityAccessManager\ConsentStore</c>, Eintrag in Benutzung, solange
/// <c>LastUsedTimeStop</c> 0 ist. Änderungen meldet <c>RegNotifyChangeKeyValue</c>, ohne Abfragen.
/// </summary>
public static class ConsentStore
{
    public const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static string KeyName(Capability capability) => capability == Capability.Camera ? "webcam" : "microphone";

    public static bool InUse(long lastStart, long lastStop) => lastStart > 0 && lastStop == 0;

    /// <summary>
    /// Anzeigename aus dem Schlüsselnamen: Paketfamilie (<c>Microsoft.WindowsCamera_8wekyb3d8bbwe</c>
    /// → „Windows Camera“) oder Pfad mit <c>#</c> statt <c>\</c> (<c>C:#…#Teams.exe</c> → „Teams“).
    /// </summary>
    public static string AppName(string keyName, bool packaged)
    {
        if (!packaged)
        {
            // Unabhängig vom Pfadtrenner des Systems: Name nach dem letzten „#“, ohne Endung.
            var file = keyName[(keyName.LastIndexOfAny(['#', '\\', '/']) + 1)..];
            var dot = file.LastIndexOf('.');
            file = dot > 0 ? file[..dot] : file;
            return file.Length > 0 ? file : keyName;
        }
        var family = keyName.Split('_')[0];
        var last = family[(family.LastIndexOf('.') + 1)..];
        return SplitWords(last.Length > 0 ? last : family);
    }

    /// <summary>„WindowsCamera“ → „Windows Camera“; Abkürzungen wie „MSTeams“ bleiben zusammen.</summary>
    public static string SplitWords(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
            {
                builder.Append(' ');
            }
            builder.Append(name[i]);
        }
        return builder.ToString();
    }
}

internal sealed partial class ConsentStoreSource : ICapabilitySource
{
    private const uint NotifyChangeName = 0x1;
    private const uint NotifyChangeLastSet = 0x4;

    private readonly List<Watch> _watches = [];
    private readonly Dictionary<string, string> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ConsentStoreSource()
    {
        foreach (var capability in Enum.GetValues<Capability>())
        {
            var key = Registry.CurrentUser.OpenSubKey(ConsentStore.Root + "\\" + ConsentStore.KeyName(capability));
            if (key is null)
            {
                Core.Log.Info($"Kein Eintrag für {capability} im ConsentStore");
                continue;
            }
            var watch = new Watch(capability, key, new AutoResetEvent(false));
            _watches.Add(watch);
            Arm(watch);
            watch.Wait = ThreadPool.RegisterWaitForSingleObject(watch.Signal, (state, _) =>
            {
                var w = (Watch)state!;
                if (_disposed)
                {
                    return;
                }
                // Die Meldung gilt nur einmal; vor dem Lesen neu anmelden, damit nichts verloren geht.
                Arm(w);
                Changed?.Invoke();
            }, watch, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    public event Action? Changed;

    private sealed record Watch(Capability Capability, RegistryKey Key, AutoResetEvent Signal)
    {
        public RegisteredWaitHandle? Wait { get; set; }
    }

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(
        SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint filter,
        SafeWaitHandle signal,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    private static void Arm(Watch watch)
    {
        try
        {
            var result = RegNotifyChangeKeyValue(watch.Key.Handle, true, NotifyChangeName | NotifyChangeLastSet, watch.Signal.SafeWaitHandle, true);
            if (result != 0)
            {
                Core.Log.Warn($"Änderungen an {watch.Capability} lassen sich nicht beobachten (Fehler {result})");
            }
        }
        catch (ObjectDisposedException)
        {
            // Beim Beenden: Die Meldung kam noch, während Schlüssel und Signal schon geschlossen wurden.
            // Eine Ausnahme im Threadpool-Rückruf würde den ganzen Prozess mitnehmen.
        }
    }

    public IReadOnlyList<CapabilityUse> Read()
    {
        var uses = new List<CapabilityUse>();
        foreach (var watch in _watches)
        {
            try
            {
                foreach (var name in watch.Key.GetSubKeyNames())
                {
                    using var sub = watch.Key.OpenSubKey(name);
                    if (sub is null)
                    {
                        continue;
                    }
                    if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var app in sub.GetSubKeyNames())
                        {
                            using var appKey = sub.OpenSubKey(app);
                            if (appKey is not null && IsInUse(appKey))
                            {
                                uses.Add(new CapabilityUse(watch.Capability, Describe(app)));
                            }
                        }
                    }
                    else if (IsInUse(sub))
                    {
                        uses.Add(new CapabilityUse(watch.Capability, ConsentStore.AppName(name, packaged: true)));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Core.Log.Warn($"ConsentStore für {watch.Capability} nicht lesbar", ex);
            }
        }
        return uses.Distinct().ToList();
    }

    private static bool IsInUse(RegistryKey key) =>
        ConsentStore.InUse(Convert.ToInt64(key.GetValue("LastUsedTimeStart") ?? 0L), Convert.ToInt64(key.GetValue("LastUsedTimeStop") ?? 0L));

    /// <summary>Dateibeschreibung der EXE („Microsoft Teams“), sonst der Dateiname.</summary>
    private string Describe(string keyName)
    {
        lock (_descriptions)
        {
            if (_descriptions.TryGetValue(keyName, out var cached))
            {
                return cached;
            }
            var name = ConsentStore.AppName(keyName, packaged: false);
            try
            {
                var path = keyName.Replace('#', '\\');
                if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 and <= 40 } description)
                {
                    name = description.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            _descriptions[keyName] = name;
            return name;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var watch in _watches)
        {
            watch.Wait?.Unregister(null);
            watch.Key.Dispose();
            watch.Signal.Dispose();
        }
        _watches.Clear();
    }
}

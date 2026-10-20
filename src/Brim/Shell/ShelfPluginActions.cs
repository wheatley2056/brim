namespace Brim.Shell;

/// <summary>
/// Brücke von der Ablage zu den Plugins (Slot <c>shelf.action</c>): Die App setzt beides beim Start, die Ablage
/// fragt beim Öffnen des Kontextmenüs, welche Aktionen gerade angeboten werden.
/// </summary>
internal static class ShelfPluginActions
{
    /// <summary>Plugin, Name des Plugins, Aktions-ID und Beschriftung.</summary>
    public static Func<IReadOnlyList<(string PluginId, string PluginName, string ActionId, string Label)>> List { get; set; } = () => [];

    public static Action<string, string, IReadOnlyList<string>> Invoke { get; set; } = (_, _, _) => { };
}

using System.Text.Json;
using Brim.Core.Widgets;

namespace Brim.Core.Plugins;

// Plugin-Protokoll, Version 1 (Plan 4.2, docs/plugins.md): JSON-RPC 2.0, eine Nachricht pro Zeile,
// über eine Named Pipe, deren Namen Brim dem Plugin in BRIM_PIPE mitgibt.

public static class PluginProtocol
{
    public const string ApiVersion = "1";

    /// <summary>Umgebungsvariable mit dem Namen der Pipe.</summary>
    public const string PipeVariable = "BRIM_PIPE";

    /// <summary>Umgebungsvariable mit der Kennung des Plugins.</summary>
    public const string IdVariable = "BRIM_PLUGIN_ID";

    // Plugin → Brim (Anfragen)
    public const string Hello = "hello";
    public const string ActivityStart = "activity.start";
    public const string ActivityUpdate = "activity.update";
    public const string ActivityEnd = "activity.end";
    public const string HudShow = "hud.show";
    public const string ViewSet = "view.set";
    public const string ViewClear = "view.clear";
    public const string LogMessage = "log";

    // Brim → Plugin (Benachrichtigungen)
    public const string ActionInvoked = "action.invoked";
    public const string ShelfItems = "shelf.items";
    public const string SettingsChanged = "settings.changed";
    public const string TabVisibility = "tab.visibility";
    public const string Shutdown = "shutdown";
}

public sealed record HelloParams
{
    public string Api { get; init; } = PluginProtocol.ApiVersion;
}

public sealed record HelloResult
{
    public required string Id { get; init; }
    public string Api { get; init; } = PluginProtocol.ApiVersion;
    public Dictionary<string, JsonElement> Settings { get; init; } = [];
    public List<string> Permissions { get; init; } = [];
}

/// <summary>Aktivität eines Plugins; Felder wie <see cref="Activity"/>.</summary>
public sealed record ActivityParams
{
    public required string Id { get; init; }
    public ActivityPriority Priority { get; init; } = ActivityPriority.Ongoing;
    public Widget? Leading { get; init; }
    public Widget? Trailing { get; init; }
    public Widget? Content { get; init; }
    public Widget? Expanded { get; init; }
    public Widget? Bead { get; init; }
    public string? Accent { get; init; }
    public int? DurationMs { get; init; }

    /// <summary>0–9; eingebaute Module wie Musik stehen darüber.</summary>
    public int Rank { get; init; }

    public bool Background { get; init; }
    public string? Announcement { get; init; }
}

public sealed record IdParams(string Id);

/// <summary>Kurze Meldung: entweder fertiger Inhalt oder Symbol, Text und optional ein Wert (0–1) als Leiste.</summary>
public sealed record HudParams
{
    public string Id { get; init; } = "hud";
    public string? Glyph { get; init; }
    public string? Text { get; init; }
    public double? Value { get; init; }
    public Widget? Content { get; init; }
    public int? DurationMs { get; init; }
    public string? Announcement { get; init; }
}

/// <summary>Inhalt im Tab „Plugins“ (Slot <c>expanded.tab</c>).</summary>
public sealed record ViewParams
{
    public required string Id { get; init; }
    public required Widget Content { get; init; }

    /// <summary>0–99, kleinere Werte weiter oben.</summary>
    public int Order { get; init; }
}

public sealed record LogParams(string Message)
{
    public string Level { get; init; } = "info";
}

public sealed record ActionParams(string ActionId, double? Value);

public sealed record ShelfItemsParams(string ActionId, IReadOnlyList<string> Paths);

public sealed record SettingsParams(Dictionary<string, JsonElement> Settings);

public sealed record VisibilityParams(bool Visible);

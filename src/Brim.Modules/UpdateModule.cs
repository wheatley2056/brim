using System.Text.Json;
using Brim.Core;
using Brim.Core.Widgets;

namespace Brim.Modules;

/// <summary>Version und Changelog eines angewendeten Updates.</summary>
public sealed record UpdateNotes(string Version, string Markdown);

/// <summary>
/// Hinweis nach einem Update: kompakt „Neu in x.y“, aufgeklappt der Changelog. Verschwindet mit
/// „Verstanden“ oder nach einem Tag. Das Update selbst ist bis dahin schon unbemerkt passiert.
/// </summary>
public sealed class UpdateModule : IBrimModule
{
    public const string ModuleId = "update";

    private static readonly TimeSpan ShowFor = TimeSpan.FromDays(1);

    private readonly string _file;
    private readonly string? _currentVersion;
    private ModuleContext? _context;

    public UpdateModule(string dataDirectory, string? currentVersion)
    {
        _file = NotesFile(dataDirectory);
        _currentVersion = currentVersion;
    }

    public static string NotesFile(string dataDirectory) => Path.Combine(dataDirectory, "update-notes.json");

    public string Id => ModuleId;
    public string DisplayName => "Updates";

    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        var notes = Read();
        if (notes is null)
        {
            return Task.CompletedTask;
        }
        // Nur zeigen, wenn das Update wirklich angekommen ist (sonst wäre der Neustart gescheitert).
        if (_currentVersion is not null && notes.Version != _currentVersion)
        {
            Log.Warn($"Updates: {notes.Version} erwartet, läuft aber {_currentVersion}");
            Dismiss();
            return Task.CompletedTask;
        }
        Show(notes);
        return Task.CompletedTask;
    }

    private UpdateNotes? Read()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<UpdateNotes>(File.ReadAllText(_file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void Show(UpdateNotes notes)
    {
        var lines = Changelog.ToWidgets(notes.Markdown);
        if (lines.Count == 0)
        {
            lines.Add(new TextWidget("Verbesserungen und Fehlerbehebungen.") { Style = TextStyle.Secondary });
        }
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "notes",
            Priority = ActivityPriority.Ongoing,
            Rank = 50, // vor Musik: einmal gesehen werden, dann mit „Verstanden“ weg
            Duration = ShowFor,
            Announcement = $"Brim wurde auf {notes.Version} aktualisiert",
            Leading = new IconWidget(Glyphs.Updated) { Size = 15, Color = BrimColors.DefaultAccent },
            Trailing = new TextWidget($"Neu in {notes.Version}") { Style = TextStyle.Mono, FontSize = 12 },
            Bead = new IconWidget(Glyphs.Updated) { Size = 14, Color = BrimColors.DefaultAccent },
            Expanded = new StackWidget(
                new StackWidget(
                    new TextWidget($"Neu in Brim {notes.Version}") { Style = TextStyle.Title, Grow = 1 },
                    new ButtonWidget("dismiss") { Kind = ButtonKind.Secondary, Label = "Verstanden" })
                {
                    Spacing = 12,
                },
                new ListWidget(lines) { Spacing = 4, Scrollable = true, Grow = 1 })
            {
                Orientation = StackOrientation.Vertical,
                Spacing = 10,
                Align = CrossAlign.Stretch,
            },
        });
    }

    public void OnAction(ActionInvocation invocation)
    {
        if (invocation.ActionId == "dismiss")
        {
            Dismiss();
        }
    }

    private void Dismiss()
    {
        _context?.End("notes");
        try
        {
            File.Delete(_file);
        }
        catch (IOException)
        {
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Einfaches Markdown aus dem Changelog in Zeilen-Widgets: Überschriften und Aufzählungen.</summary>
public static class Changelog
{
    public static List<Widget> ToWidgets(string markdown)
    {
        var result = new List<Widget>();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith('#'))
            {
                result.Add(new TextWidget(Inline(line.TrimStart('#').Trim())) { Style = TextStyle.Body, FontSize = 14, Wrap = true });
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                result.Add(new TextWidget("•  " + Inline(line[2..].Trim())) { Style = TextStyle.Secondary, Wrap = true });
            }
            else
            {
                result.Add(new TextWidget(Inline(line)) { Style = TextStyle.Secondary, Wrap = true });
            }
        }
        return result;
    }

    /// <summary>Fett, kursiv, Code und Links auf reinen Text reduzieren.</summary>
    private static string Inline(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text.Replace("**", "").Replace("__", "").Replace("`", ""),
            @"\[([^\]]+)\]\([^)]+\)", "$1");
}

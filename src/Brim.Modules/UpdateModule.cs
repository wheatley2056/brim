using System.Text.Json;
using Brim.Core;
using Brim.Core.Widgets;

namespace Brim.Modules;

/// <summary>Version und Changelog eines angewendeten Updates.</summary>
public sealed record UpdateNotes(string Version, string Markdown);

/// <summary>
/// Updates in der Notch. Liegt ein neues bereit, klappt kurz ein Hinweis auf; danach bleibt kompakt
/// „Update x.y“ stehen, aufgeklappt mit Changelog und „Jetzt installieren“. Nach dem Update steht
/// kompakt „Neu in x.y“, aufgeklappt der Changelog; das verschwindet mit „Verstanden“ oder nach einem Tag.
/// </summary>
public sealed class UpdateModule : IBrimModule
{
    public const string ModuleId = "update";
    public const string Install = "install";
    public const string Later = "later";

    private static readonly TimeSpan ShowFor = TimeSpan.FromDays(1);

    private readonly string _file;
    private readonly string? _currentVersion;
    private ModuleContext? _context;
    private UpdateNotes? _available;
    private bool _installing;

    /// <summary>„Jetzt installieren“ gedrückt: das geladene Update einspielen und neu starten.</summary>
    public Action? InstallRequested { get; set; }

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

    /// <summary>Ein neues Update liegt bereit: sofort Bescheid geben und den Knopf zum Installieren zeigen.</summary>
    public void ShowAvailable(UpdateNotes notes)
    {
        _available = notes;
        _installing = false;
        // Kurz von selbst aufklappen, damit es auffällt; der Hinweis bleibt danach kompakt stehen.
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "available-alert",
            Priority = ActivityPriority.Critical,
            Duration = TimeSpan.FromSeconds(10),
            Announcement = $"Brim {notes.Version} ist da",
            Content = new StackWidget(
                new IconWidget(Glyphs.Download) { Size = 24, Color = BrimColors.DefaultAccent },
                new StackWidget(
                    new TextWidget($"Brim {notes.Version} ist da") { Style = TextStyle.Title, FontSize = 15 },
                    new TextWidget("Installieren dauert nur ein paar Sekunden.") { Style = TextStyle.Secondary })
                {
                    Orientation = StackOrientation.Vertical,
                    Spacing = 2,
                    Align = CrossAlign.Start,
                    Grow = 1,
                },
                new ButtonWidget(Install) { Kind = ButtonKind.Primary, Label = "Installieren" },
                new ButtonWidget(Later) { Kind = ButtonKind.Secondary, Label = "Später" })
            {
                Spacing = 10,
            },
        });
        PostAvailable();
    }

    private void PostAvailable()
    {
        if (_available is not { } notes)
        {
            return;
        }
        var lines = Changelog.ToWidgets(notes.Markdown);
        if (lines.Count == 0)
        {
            lines.Add(new TextWidget("Verbesserungen und Fehlerbehebungen.") { Style = TextStyle.Secondary });
        }
        Widget install = _installing
            ? new TextWidget("Wird installiert …") { Style = TextStyle.Secondary }
            : new ButtonWidget(Install) { Kind = ButtonKind.Primary, Label = "Jetzt installieren" };
        _context?.Post(new Activity
        {
            ModuleId = ModuleId,
            Id = "available",
            Priority = ActivityPriority.Ongoing,
            Rank = 50,
            Leading = new IconWidget(Glyphs.Download) { Size = 15, Color = BrimColors.DefaultAccent },
            Trailing = new TextWidget($"Update {notes.Version}") { Style = TextStyle.Mono, FontSize = 12 },
            Bead = new IconWidget(Glyphs.Download) { Size = 14, Color = BrimColors.DefaultAccent },
            Expanded = new StackWidget(
                new StackWidget(
                    new TextWidget($"Brim {notes.Version} ist da") { Style = TextStyle.Title, Grow = 1 },
                    new ButtonWidget(Later) { Kind = ButtonKind.Secondary, Label = "Später" },
                    install)
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

    /// <summary>Installieren hat nicht geklappt: den Knopf wieder anbieten.</summary>
    public void InstallFailed()
    {
        _installing = false;
        PostAvailable();
    }

    public void OnAction(ActionInvocation invocation)
    {
        switch (invocation.ActionId)
        {
            case "dismiss":
                Dismiss();
                break;
            case Install when _available is not null && !_installing:
                _installing = true;
                _context?.End("available-alert");
                PostAvailable();
                InstallRequested?.Invoke();
                break;
            case Later:
                // Bis zum nächsten Start oder einer neueren Version Ruhe; unbemerkt eingespielt wird es trotzdem.
                _available = null;
                _context?.End("available-alert");
                _context?.End("available");
                break;
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

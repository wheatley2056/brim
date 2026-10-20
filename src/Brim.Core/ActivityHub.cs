namespace Brim.Core;

/// <summary>
/// Sammelstelle aller Aktivitäten. Module melden hier von beliebigen Threads,
/// die Shell liest Schnappschüsse und reagiert auf <see cref="Changed"/>.
/// </summary>
public sealed class ActivityHub
{
    /// <summary>Standarddauer für HUDs (Plan 3: 1,6 s).</summary>
    public static readonly TimeSpan DefaultTransientDuration = TimeSpan.FromSeconds(1.6);

    /// <summary>Standarddauer, die sich kritische Hinweise von selbst erweitern.</summary>
    public static readonly TimeSpan DefaultCriticalDuration = TimeSpan.FromSeconds(4);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActiveActivity> _entries = new();
    private readonly Dictionary<string, Action<ActionInvocation>> _actionHandlers = new();
    private readonly Dictionary<string, TabView> _views = new();
    private readonly Dictionary<object, NotchTab> _visibleTabs = new();
    private long _sequence;

    public ActivityHub(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public TimeProvider Time => _time;

    /// <summary>Wird nach jeder Änderung ausgelöst, auf dem Thread des Aufrufers.</summary>
    public event Action? Changed;

    /// <summary>Irgendeine Notch wurde auf- oder zugeklappt (an Module weitergereicht).</summary>
    public event Action<bool>? ExpandedChanged;

    private bool _expanded;

    /// <summary>Von der Shell aufgerufen; meldet nur echte Wechsel weiter. Fehler einzelner Module werden protokolliert.</summary>
    public void NotifyExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }
        _expanded = expanded;
        foreach (var handler in ExpandedChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<bool>)handler)(expanded);
            }
            catch (Exception ex)
            {
                Log.Error("Modul reagierte fehlerhaft auf Auf-/Zuklappen", ex);
            }
        }
    }

    /// <summary>Ein Tab wurde sichtbar (<c>true</c>) oder ist in keiner Notch mehr zu sehen (<c>false</c>).</summary>
    public event Action<NotchTab, bool>? TabVisibilityChanged;

    /// <summary>
    /// Von jeder Notch aufgerufen, wenn sich ihr sichtbarer Tab ändert (<c>null</c> = zugeklappt oder geschlossen).
    /// Ein Tab gilt als sichtbar, solange ihn irgendeine Notch zeigt; gemeldet werden nur echte Wechsel.
    /// </summary>
    public void NotifyVisibleTab(object source, NotchTab? tab)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<(NotchTab Tab, bool Visible)> changes = [];
        lock (_gate)
        {
            var before = _visibleTabs.Values.ToHashSet();
            if (tab is { } t)
            {
                _visibleTabs[source] = t;
            }
            else
            {
                _visibleTabs.Remove(source);
            }
            var after = _visibleTabs.Values.ToHashSet();
            changes.AddRange(after.Except(before).Select(x => (x, true)));
            changes.AddRange(before.Except(after).Select(x => (x, false)));
        }
        foreach (var (changed, visible) in changes)
        {
            foreach (var handler in TabVisibilityChanged?.GetInvocationList() ?? [])
            {
                try
                {
                    ((Action<NotchTab, bool>)handler)(changed, visible);
                }
                catch (Exception ex)
                {
                    Log.Error("Modul reagierte fehlerhaft auf einen Tabwechsel", ex);
                }
            }
        }
    }

    public bool IsTabVisible(NotchTab tab)
    {
        lock (_gate)
        {
            return _visibleTabs.ContainsValue(tab);
        }
    }

    /// <summary>Setzt oder ersetzt den Inhalt eines Moduls in einem Tab.</summary>
    public void SetView(TabView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            _views[view.Key] = view;
        }
        Changed?.Invoke();
    }

    public void RemoveView(string moduleId, string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _views.Remove(ActivityKey.Of(moduleId, id));
        }
        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Inhalte eines Tabs, nach Spalte und Reihenfolge sortiert.</summary>
    public IReadOnlyList<TabView> Views(NotchTab tab)
    {
        lock (_gate)
        {
            return _views.Values
                .Where(v => v.Tab == tab)
                .OrderBy(v => v.Column)
                .ThenBy(v => v.Order)
                .ThenBy(v => v.Key, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Startet oder aktualisiert eine Aktivität.</summary>
    /// <remarks>
    /// Vorübergehende und kritische Aktivitäten bekommen bei jedem Aufruf eine neue Ablaufzeit,
    /// so bleibt das Lautstärke-HUD stehen, solange der Regler bewegt wird.
    /// </remarks>
    public void Post(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            DateTimeOffset? expires = activity.Priority switch
            {
                ActivityPriority.Transient => now + (activity.Duration ?? DefaultTransientDuration),
                ActivityPriority.Critical => now + (activity.Duration ?? DefaultCriticalDuration),
                _ => activity.Duration is { } d ? now + d : null,
            };

            if (_entries.TryGetValue(activity.Key, out var existing) && !IsExpired(existing, now))
            {
                _entries[activity.Key] = existing with { Activity = activity, UpdatedAt = now, ExpiresAt = expires };
            }
            else
            {
                _entries[activity.Key] = new ActiveActivity(activity, now, now, expires, ++_sequence);
            }
        }
        Changed?.Invoke();
    }

    public void End(string moduleId, string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _entries.Remove(ActivityKey.Of(moduleId, id));
        }
        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Entfernt alle Aktivitäten und Tab-Inhalte eines Moduls, z. B. wenn es abstürzt oder abgeschaltet wird.</summary>
    public void EndAll(string moduleId)
    {
        bool removed;
        lock (_gate)
        {
            var keys = _entries.Values.Where(e => e.Activity.ModuleId == moduleId).Select(e => e.Key).ToList();
            keys.ForEach(k => _entries.Remove(k));
            var views = _views.Values.Where(v => v.ModuleId == moduleId).Select(v => v.Key).ToList();
            views.ForEach(k => _views.Remove(k));
            removed = keys.Count > 0 || views.Count > 0;
        }
        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Alle nicht abgelaufenen Aktivitäten. Abgelaufene werden dabei aufgeräumt.</summary>
    public IReadOnlyList<ActiveActivity> Snapshot()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var key in _entries.Values.Where(e => IsExpired(e, now)).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }
            return _entries.Values.ToList();
        }
    }

    /// <summary>Nächster Zeitpunkt, an dem eine Aktivität abläuft; die Shell plant dafür eine Neubewertung.</summary>
    public DateTimeOffset? NextExpiry()
    {
        lock (_gate)
        {
            return _entries.Values.Where(e => e.ExpiresAt is not null).Min(e => e.ExpiresAt);
        }
    }

    public void RegisterActionHandler(string moduleId, Action<ActionInvocation> handler)
    {
        lock (_gate)
        {
            _actionHandlers[moduleId] = handler;
        }
    }

    public void UnregisterActionHandler(string moduleId)
    {
        lock (_gate)
        {
            _actionHandlers.Remove(moduleId);
        }
    }

    /// <summary>Leitet eine Aktion an das zuständige Modul weiter. Fehler im Modul werden protokolliert, nicht geworfen.</summary>
    public void Invoke(ActionInvocation invocation)
    {
        Action<ActionInvocation>? handler;
        lock (_gate)
        {
            _actionHandlers.TryGetValue(invocation.ModuleId, out handler);
        }
        if (handler is null)
        {
            Log.Warn($"Keine Aktion-Behandlung für Modul {invocation.ModuleId}");
            return;
        }
        try
        {
            handler(invocation);
        }
        catch (Exception ex)
        {
            Log.Error($"Aktion {invocation.ActionId} in {invocation.ModuleId} fehlgeschlagen", ex);
        }
    }

    private static bool IsExpired(ActiveActivity entry, DateTimeOffset now) => entry.ExpiresAt is { } e && e <= now;
}

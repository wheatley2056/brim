using Brim.Core.Widgets;

namespace Brim.Core;

/// <summary>Spalte im Tab „System“: links die Messwerte, rechts schmalere Karten (Plan 4.6, 4.7).</summary>
public enum TabColumn { Main, Side }

/// <summary>
/// Fester Inhalt eines Moduls in einem Tab, unabhängig von Aktivitäten (Plan 4.2: <c>view.set</c>
/// für den Slot <c>expanded.tab</c>). Mehrere Module teilen sich einen Tab, z. B. Akku und Ressourcen.
/// </summary>
public sealed record TabView
{
    public required string ModuleId { get; init; }
    public required string Id { get; init; }
    public required Widget Content { get; init; }
    public NotchTab Tab { get; init; } = NotchTab.System;
    public TabColumn Column { get; init; } = TabColumn.Main;

    /// <summary>Kleinere Werte stehen weiter oben.</summary>
    public int Order { get; init; }

    /// <summary>
    /// Detailansicht: verdrängt alle anderen Karten des Tabs, nimmt die volle Breite ein, und die Notch wird höher
    /// (z. B. CPU nach einem Klick auf ihre Karte). Nur eine je Tab.
    /// </summary>
    public bool IsDetail { get; init; }

    public string Key => ActivityKey.Of(ModuleId, Id);
}

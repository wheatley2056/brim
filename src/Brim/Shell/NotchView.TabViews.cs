using Brim.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TabView = Brim.Core.TabView;

namespace Brim.Shell;

/// <summary>
/// Tabs aus Karten: „System“ und „Plugins“. Module und Plugins liefern Karten über
/// <see cref="ActivityHub.SetView"/>; links die Hauptspalte (328 px, ohne rechte Karten die volle Breite),
/// rechts schmale Karten (Plugin-Verwaltung), unten eine Reihe Messwerte (Design „Windows-11-nativ“:
/// Kacheln, Regler, darunter Akku, CPU, Arbeitsspeicher, Netzwerk). Jede Karte hat einen eigenen Platz,
/// damit ihre Knöpfe beim richtigen Modul landen.
/// </summary>
public sealed partial class NotchView
{
    private const double MainColumnWidth = 328;
    private readonly Dictionary<string, ContentPresenter> _viewPresenters = new();

    /// <returns><c>false</c>, wenn kein Modul etwas beisteuert (dann zeigt der Tab einen Hinweis).</returns>
    private bool UpdateTabViews(NotchTab tab)
    {
        var views = _hub.Views(tab);
        // Eine Detailansicht verdrängt alle anderen Karten und nimmt die volle Breite.
        if (views.FirstOrDefault(v => v.IsDetail) is { } detail)
        {
            views = [detail];
        }
        var main = views.Where(v => v.Column == TabColumn.Main || v.IsDetail).ToList();
        var side = views.Where(v => v.Column == TabColumn.Side && !v.IsDetail).ToList();
        var cards = views.Where(v => v.Column == TabColumn.Cards && !v.IsDetail).ToList();
        Fill(TabViewMain, main);
        Fill(TabViewSide, side);
        Fill(TabViewCards, cards, horizontal: true);
        TabViewCards.Margin = new Thickness(0, cards.Count > 0 && main.Count + side.Count > 0 ? 16 : 0, 0, 0);

        // Fehlt eine Spalte, nimmt die andere die ganze Breite.
        TabViewMainColumn.Width = main.Count == 0 ? new GridLength(0)
            : side.Count == 0 ? new GridLength(1, GridUnitType.Star)
            : new GridLength(MainColumnWidth);
        TabViewSideColumn.Width = side.Count == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TabViewPanel.ColumnSpacing = main.Count > 0 && side.Count > 0 ? 16 : 0;

        // Karten, die es nicht mehr gibt (z. B. Akku entfernt), freigeben.
        foreach (var gone in _viewPresenters.Keys.Except(views.Select(v => v.Key)).ToList())
        {
            _viewPresenters.Remove(gone);
            _slots.Remove("view:" + gone);
        }
        return views.Count > 0;
    }

    /// <param name="horizontal">Nebeneinander in gleich breiten Spalten (Messwert-Karten) statt untereinander.</param>
    private void Fill(Grid column, IReadOnlyList<TabView> views, bool horizontal = false)
    {
        column.RowDefinitions.Clear();
        column.ColumnDefinitions.Clear();
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            var grow = view.Content.Grow;
            if (horizontal)
            {
                column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                column.RowDefinitions.Add(new RowDefinition { Height = grow > 0 ? new GridLength(grow, GridUnitType.Star) : GridLength.Auto });
            }

            if (!_viewPresenters.TryGetValue(view.Key, out var presenter))
            {
                presenter = new ContentPresenter
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                };
                _viewPresenters[view.Key] = presenter;
            }
            if (!ReferenceEquals(presenter.Parent, column))
            {
                (presenter.Parent as Panel)?.Children.Remove(presenter);
                column.Children.Add(presenter);
            }
            Grid.SetRow(presenter, horizontal ? 0 : i);
            Grid.SetColumn(presenter, horizontal ? i : 0);
            Render("view:" + view.Key, presenter, view.ModuleId, view.Content);
        }

        var keep = views.Select(v => _viewPresenters[v.Key]).ToHashSet();
        foreach (var stale in column.Children.Where(c => !keep.Contains(c)).ToList())
        {
            column.Children.Remove(stale);
        }
    }
}

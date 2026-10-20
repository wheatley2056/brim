using Brim.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TabView = Brim.Core.TabView;

namespace Brim.Shell;

/// <summary>
/// Tab „System“ (design/boards/4-shelf-system.html): Module liefern Karten über
/// <see cref="ActivityHub.SetView"/>. Links die Messwerte (328 px), rechts Akku und Prozesse.
/// Jede Karte hat einen eigenen Platz, damit ihre Knöpfe beim richtigen Modul landen.
/// </summary>
public sealed partial class NotchView
{
    private const double SystemMainWidth = 328;
    private readonly Dictionary<string, ContentPresenter> _viewPresenters = new();

    /// <returns><c>false</c>, wenn kein Modul etwas beisteuert (dann zeigt der Tab einen Hinweis).</returns>
    private bool UpdateSystem()
    {
        var views = _hub.Views(NotchTab.System);
        var main = views.Where(v => v.Column == TabColumn.Main).ToList();
        var side = views.Where(v => v.Column == TabColumn.Side).ToList();
        Fill(SystemMain, main);
        Fill(SystemSide, side);

        // Fehlt eine Spalte, nimmt die andere die ganze Breite.
        SystemMainColumn.Width = main.Count == 0 ? new GridLength(0)
            : side.Count == 0 ? new GridLength(1, GridUnitType.Star)
            : new GridLength(SystemMainWidth);
        SystemSideColumn.Width = side.Count == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SystemPanel.ColumnSpacing = main.Count > 0 && side.Count > 0 ? 16 : 0;

        // Karten, die es nicht mehr gibt (z. B. Akku entfernt), freigeben.
        foreach (var gone in _viewPresenters.Keys.Except(views.Select(v => v.Key)).ToList())
        {
            _viewPresenters.Remove(gone);
            _slots.Remove("view:" + gone);
        }
        return views.Count > 0;
    }

    private void Fill(Grid column, IReadOnlyList<TabView> views)
    {
        column.RowDefinitions.Clear();
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            var grow = view.Content.Grow;
            column.RowDefinitions.Add(new RowDefinition { Height = grow > 0 ? new GridLength(grow, GridUnitType.Star) : GridLength.Auto });

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
            Grid.SetRow(presenter, i);
            Render("view:" + view.Key, presenter, view.ModuleId, view.Content);
        }

        var keep = views.Select(v => _viewPresenters[v.Key]).ToHashSet();
        foreach (var stale in column.Children.Where(c => !keep.Contains(c)).ToList())
        {
            column.Children.Remove(stale);
        }
    }
}

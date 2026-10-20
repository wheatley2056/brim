namespace Brim.Core.Widgets;

/// <summary>Hilfen für ganze Bausteinbäume, z. B. um Inhalte von Plugins zu prüfen und ihre Aktionen zuzuordnen.</summary>
public static class WidgetTree
{
    /// <summary>Ersetzt jede Aktions-ID im Baum (Knöpfe, Schalter, Regler).</summary>
    public static Widget MapActions(Widget widget, Func<string, string> map) => widget switch
    {
        ButtonWidget b => b with { ActionId = map(b.ActionId) },
        SwitchWidget s => s with { ActionId = map(s.ActionId) },
        ProgressWidget { SeekActionId: { } id } p => p with { SeekActionId = map(id) },
        StackWidget s => s with { Children = s.Children.Select(c => MapActions(c, map)).ToList(), ActionId = s.ActionId is { } id ? map(id) : null },
        ListWidget l => l with { Items = l.Items.Select(c => MapActions(c, map)).ToList() },
        _ => widget,
    };

    /// <summary>Alle Bausteine im Baum, der Wurzel zuerst.</summary>
    public static IEnumerable<Widget> All(Widget widget)
    {
        yield return widget;
        var children = widget switch
        {
            StackWidget s => s.Children,
            ListWidget l => l.Items,
            _ => [],
        };
        foreach (var child in children)
        {
            foreach (var w in All(child))
            {
                yield return w;
            }
        }
    }

    public static int Depth(Widget widget) => 1 + (widget switch
    {
        StackWidget s => s.Children.Select(Depth).DefaultIfEmpty(0).Max(),
        ListWidget l => l.Items.Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    });
}

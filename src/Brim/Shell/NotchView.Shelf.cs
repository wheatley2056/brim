using Brim.Core;
using Brim.Modules;
using Brim.Core.Widgets;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Brim.Shell;

/// <summary>
/// Hineinziehen in die Ablage (Plan 4.4): Die ganze Notch ist Ablagefläche. Beim Drüberziehen
/// meldet sie sich als Ziel, beim Loslassen landen die Einträge in der Ablage.
/// </summary>
public sealed partial class NotchView
{
    private ShelfStore? _shelf;
    private SettingsStore? _settingsStore;
    private ShelfView? _shelfView;

    /// <summary>Etwas wird über die Notch gezogen (true) oder wieder weggezogen (false).</summary>
    public event Action<bool>? DropTargetChanged;

    /// <summary>Etwas wurde in die Ablage gelegt.</summary>
    public event Action? Dropped;

    /// <summary>Herausziehen aus der Ablage beginnt (true) oder ist beendet (false).</summary>
    public event Action<bool>? DraggingOut;

    /// <summary>Menü oder Eingabefeld der Ablage geöffnet (true) bzw. geschlossen.</summary>
    public event Action<bool>? PopupChanged;

    private void InitializeShelf(ShelfStore shelf, SettingsStore settings, NearbyModule nearby, Func<nint> window)
    {
        _shelf = shelf;
        _settingsStore = settings;
        _shelfView = new ShelfView(shelf, settings, nearby, window);
        _shelfView.DraggingOut += active => DraggingOut?.Invoke(active);
        _shelfView.PopupChanged += open => PopupChanged?.Invoke(open);
        ShelfPresenter.Content = _shelfView;
        DropIcon.Glyph = Glyphs.Download;

        CanvasRoot.AllowDrop = true;
        CanvasRoot.DragEnter += OnDragEnter;
        CanvasRoot.DragOver += OnDragOver;
        CanvasRoot.DragLeave += (_, _) => DropTargetChanged?.Invoke(false);
        CanvasRoot.Drop += OnDrop;
    }

    private static bool CanAccept(DragEventArgs e) => e.DataView.Contains(StandardDataFormats.StorageItems);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!CanAccept(e))
        {
            return;
        }
        DropTargetChanged?.Invoke(true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!CanAccept(e))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "In die Ablage";
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!CanAccept(e) || _shelf is null || _settingsStore is null)
        {
            DropTargetChanged?.Invoke(false);
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = await ResolvePathsAsync(items);
            var added = _shelf.Add(paths, _settingsStore.Current.Shelf.Storage);
            Log.Info($"Ablage: {added.Count} neu");
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            Log.Warn("Ablegen fehlgeschlagen", ex);
        }
        finally
        {
            deferral.Complete();
        }
        Dropped?.Invoke();
    }

    /// <summary>
    /// Echte Pfade direkt; Einträge ohne Pfad (z. B. Mail-Anhänge, Dateien aus Apps) werden zuerst
    /// in einen Temp-Ordner kopiert – die Ablage erkennt ihn als flüchtig und übernimmt eine Kopie.
    /// </summary>
    private static async Task<List<string>> ResolvePathsAsync(IReadOnlyList<IStorageItem> items)
    {
        var paths = new List<string>();
        StorageFolder? staging = null;
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                paths.Add(item.Path);
                continue;
            }
            if (item is StorageFile file)
            {
                staging ??= await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath())
                    .AsTask().ContinueWith(t => t.Result.CreateFolderAsync("Brim-" + Guid.NewGuid().ToString("N")[..8]).AsTask()).Unwrap();
                var copy = await file.CopyAsync(staging, file.Name, NameCollisionOption.GenerateUniqueName);
                paths.Add(copy.Path);
            }
        }
        return paths;
    }

    /// <summary>Ablagefläche aus dem Design: gestrichelt, Symbol und Hinweis.</summary>
    private FrameworkElement DropLayerFor()
    {
        DropOutline.Stroke = Brushes.Get("#5A5D66");
        return DropLayer;
    }
}

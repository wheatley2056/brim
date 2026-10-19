using Brim.Core;

namespace Brim.Core.Tests;

public sealed class ShelfStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "brim-shelf-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _documents;
    private readonly string _temp;

    public ShelfStoreTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _documents = Directory.CreateDirectory(Path.Combine(_root, "documents")).FullName;
        _temp = Directory.CreateDirectory(Path.Combine(_root, "temp")).FullName;
    }

    private ShelfStore Store() => new(_data, [_temp]);

    private string File(string folder, string name, string content = "x")
    {
        var path = Path.Combine(folder, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Reference_keeps_original_path()
    {
        var path = File(_documents, "brief.pdf");

        var item = Assert.Single(Store().Add([path], ShelfStorage.Reference));

        Assert.Equal(ShelfItemMode.Reference, item.Mode);
        Assert.Equal(path, item.Path);
    }

    [Fact]
    public void Temporary_files_are_copied_automatically()
    {
        var path = File(_temp, "anhang.docx", "inhalt");

        var item = Assert.Single(Store().Add([path], ShelfStorage.Reference));

        Assert.Equal(ShelfItemMode.Copy, item.Mode);
        Assert.NotEqual(path, item.Path);
        Assert.Equal("anhang.docx", item.Name);
        Assert.Equal("inhalt", System.IO.File.ReadAllText(item.Path));
    }

    [Fact]
    public void Same_path_is_not_added_twice()
    {
        var path = File(_documents, "a.txt");
        var store = Store();

        store.Add([path], ShelfStorage.Reference);
        Assert.Empty(store.Add([path], ShelfStorage.Reference));
        Assert.Single(store.Items);
    }

    [Fact]
    public void Items_survive_restart()
    {
        var path = File(_documents, "bleibt.txt");
        Store().Add([path], ShelfStorage.Reference);

        var reloaded = Store();
        reloaded.Load();

        Assert.Equal(path, Assert.Single(reloaded.Items).Path);
    }

    [Fact]
    public void Removing_deletes_copies_but_never_originals()
    {
        var original = File(_documents, "original.txt");
        var temporary = File(_temp, "kopie.txt");
        var store = Store();
        store.Add([original], ShelfStorage.Reference);
        var copy = store.Add([temporary], ShelfStorage.Reference)[0];

        store.Clear();

        Assert.Empty(store.Items);
        Assert.True(System.IO.File.Exists(original));
        Assert.False(System.IO.File.Exists(copy.Path));
    }

    [Fact]
    public void Missing_reference_is_reported()
    {
        var path = File(_documents, "weg.txt");
        var item = Store().Add([path], ShelfStorage.Reference)[0];

        System.IO.File.Delete(path);

        Assert.False(item.Exists);
    }

    [Fact]
    public void Folders_can_be_copied()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temp, "Screenshots")).FullName;
        File(folder, "1.png");
        Directory.CreateDirectory(Path.Combine(folder, "alt"));
        File(Path.Combine(folder, "alt"), "2.png");

        var item = Assert.Single(Store().Add([folder], ShelfStorage.Reference));

        Assert.True(item.IsFolder);
        Assert.True(System.IO.File.Exists(Path.Combine(item.Path, "alt", "2.png")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

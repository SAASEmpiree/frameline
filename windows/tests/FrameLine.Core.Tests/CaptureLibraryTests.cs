using Xunit;

namespace FrameLine.Core.Tests;

public class CaptureLibraryTests
{
    private static string Touch(TempDir tmp, string name, DateTime when)
    {
        var path = tmp.File(name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, when);
        return path;
    }

    [Fact]
    public void Lists_images_and_videos_newest_first()
    {
        using var tmp = new TempDir();
        var t = new DateTime(2026, 11, 16, 12, 0, 0);
        Touch(tmp, "old.png", t.AddMinutes(-10));
        Touch(tmp, "rec.mp4", t.AddMinutes(5));
        Touch(tmp, "mid.JPG", t);
        Touch(tmp, "clip.mov", t.AddMinutes(-20));
        Touch(tmp, "scan.tiff", t.AddMinutes(-30));

        var items = CaptureLibrary.Scan(tmp.Path);
        Assert.Equal(new[] { "rec.mp4", "mid.JPG", "old.png", "clip.mov", "scan.tiff" }, items.Select(i => i.Name));
        Assert.True(items[0].IsVideo);
        Assert.False(items[1].IsVideo);
        Assert.Equal(t, items[1].Created);
    }

    [Fact]
    public void Ignores_non_captures_hidden_files_temp_files_and_folders()
    {
        using var tmp = new TempDir();
        var t = DateTime.Now;
        Touch(tmp, "keep.png", t);
        Touch(tmp, "notes.txt", t);
        Touch(tmp, ".hidden.png", t);
        Touch(tmp, "rec.mp4.tmp", t);
        Touch(tmp, "noext", t);
        Directory.CreateDirectory(tmp.File("folder.png"));
        Assert.Equal(new[] { "keep.png" }, CaptureLibrary.Scan(tmp.Path).Select(i => i.Name));
    }

    [Fact]
    public void Same_date_orders_by_name_for_stable_output()
    {
        using var tmp = new TempDir();
        var t = new DateTime(2026, 1, 1);
        Touch(tmp, "a.png", t);
        Touch(tmp, "c.png", t);
        Touch(tmp, "b.png", t);
        Assert.Equal(new[] { "c.png", "b.png", "a.png" }, CaptureLibrary.Scan(tmp.Path).Select(i => i.Name));
    }

    [Fact]
    public void Missing_folder_is_an_empty_library()
    {
        Assert.Empty(CaptureLibrary.Scan(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Theory]
    [InlineData("a.PNG", false)]
    [InlineData("a.heic", false)]
    [InlineData("a.m4v", true)]
    [InlineData("a.MOV", true)]
    [InlineData("a.txt", null)]
    [InlineData("a", null)]
    public void Classify(string name, bool? expected) => Assert.Equal(expected, CaptureLibrary.Classify(name));

    [Fact]
    public void Reload_raises_Changed_only_when_the_listing_differs()
    {
        using var tmp = new TempDir();
        using var lib = new CaptureLibrary(() => tmp.Path);
        var raised = 0;
        lib.Changed += () => raised++;
        lib.Reload();
        Assert.Equal(1, raised);          // first load
        lib.Reload();
        Assert.Equal(1, raised);
        Touch(tmp, "x.png", DateTime.Now);
        lib.Reload();
        Assert.Equal(2, raised);
        lib.ReloadFile(tmp.File("x.png"));
        Assert.Equal(3, raised);          // forced
        Assert.Single(lib.Items);
    }

    [Fact]
    public void Import_copies_captures_with_unique_names()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        using var lib = new CaptureLibrary(() => dst.Path);
        var a = Touch(src, "shot.png", DateTime.Now);
        var b = Touch(src, "doc.txt", DateTime.Now);
        Touch(dst, "shot.png", DateTime.Now);

        var imported = lib.ImportFiles(new[] { a, b, a });
        Assert.Equal(new[] { dst.File("shot (2).png"), dst.File("shot (3).png") }, imported);
        Assert.Equal(3, lib.Items.Count);
    }

    [Fact]
    public async Task Watcher_picks_up_new_files_after_the_debounce()
    {
        using var tmp = new TempDir();
        using var lib = new CaptureLibrary(() => tmp.Path);
        lib.Start();
        var signal = new TaskCompletionSource();
        lib.Changed += () => { if (lib.Items.Count == 1) signal.TrySetResult(); };

        File.WriteAllText(tmp.File("new.png"), "x");
        File.WriteAllText(tmp.File("ignored.txt"), "x");

        var done = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(signal.Task, done);
        Assert.Equal("new.png", lib.Items[0].Name);
    }
}

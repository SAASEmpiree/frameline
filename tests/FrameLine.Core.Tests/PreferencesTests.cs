using System.Text.Json.Nodes;
using Xunit;

namespace FrameLine.Core.Tests;

public class PreferencesTests
{
    [Fact]
    public void Defaults_match_the_macOS_registered_defaults()
    {
        using var tmp = new TempDir();
        var p = new Preferences(tmp.File("settings.json"));
        Assert.Equal("png", p.ImageFormat);
        Assert.False(p.CopyToClipboard);
        Assert.True(p.PlaySound);
        Assert.True(p.ShowPreview);
        Assert.False(p.CaptureCursor);
        Assert.True(p.WindowShadow);
        Assert.Equal(30, p.RecordFps);
        Assert.Equal(15, p.GifFps);
        Assert.Equal(900, p.GifWidth);
        Assert.True(p.ExportGif);
        Assert.True(p.RecordSystemAudio);
        Assert.False(p.RecordMicrophone);
        Assert.True(p.PreviewFollowsCursor);
        Assert.Equal("bottomLeft", p.PreviewCorner);
        Assert.False(p.ClosePreviewAfterDrag);
        Assert.Equal("default", p.CaptureSoundName);
        Assert.Equal("default", p.RecordSoundName);
        Assert.True(p.McpServerEnabled);
        Assert.False(p.OnboardingCompleted);
        Assert.Equal("dark", p.Theme);
        Assert.False(p.OpenEditor);
        Assert.Equal("system", p.DisplayLanguage);
        Assert.Equal(Preferences.DefaultSaveDirectory, p.SaveDirectory);
        Assert.EndsWith("FrameLine", p.SaveDirectory);
        Assert.False(File.Exists(p.SettingsPath)); // reading never writes
    }

    [Fact]
    public void Values_round_trip_through_the_file_with_the_macOS_keys()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json");
        var p = new Preferences(path)
        {
            ImageFormat = "jpg", CopyToClipboard = true, PlaySound = false, RecordFps = 60, GifWidth = 640,
            PreviewCorner = "topRight", Theme = "light", DisplayLanguage = "fr", ExportGif = false,
            SaveDirectory = tmp.File("shots"),
        };
        var q = new Preferences(path);
        Assert.Equal("jpg", q.ImageFormat);
        Assert.True(q.CopyToClipboard);
        Assert.False(q.PlaySound);
        Assert.Equal(60, q.RecordFps);
        Assert.Equal(640, q.GifWidth);
        Assert.Equal("topRight", q.PreviewCorner);
        Assert.Equal("light", q.Theme);
        Assert.Equal("fr", q.DisplayLanguage);
        Assert.False(q.ExportGif);
        Assert.Equal(tmp.File("shots"), q.SaveDirectory);

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("jpg", (string?)json["imageFormat"]);
        Assert.Equal(false, (bool?)json["exportGIF"]);
        Assert.Equal("fr", (string?)json["displayLang"]);
        Assert.Equal(tmp.File("shots"), (string?)json["saveDirectoryPath"]);
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Empty_save_directory_returns_to_the_default()
    {
        using var tmp = new TempDir();
        var p = new Preferences(tmp.File("settings.json")) { SaveDirectory = tmp.File("x") };
        p.SaveDirectory = "";
        Assert.Equal(Preferences.DefaultSaveDirectory, p.SaveDirectory);
    }

    [Fact]
    public void Non_positive_numbers_read_as_defaults_like_the_Swift_getters()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json");
        File.WriteAllText(path, """{ "recordFps": 0, "gifFps": -3, "gifWidth": "wide" }""");
        var p = new Preferences(path);
        Assert.Equal(30, p.RecordFps);
        Assert.Equal(15, p.GifFps);
        Assert.Equal(900, p.GifWidth);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public void Corrupt_file_falls_back_to_defaults_and_is_repaired_on_next_write(string content)
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json");
        File.WriteAllText(path, content);
        var p = new Preferences(path);
        Assert.Equal("png", p.ImageFormat);
        Assert.True(p.PlaySound);
        Assert.True(File.Exists(path + ".corrupt"));

        p.Theme = "light";
        Assert.Equal("light", new Preferences(path).Theme);
    }

    [Fact]
    public void Wrong_value_types_read_as_defaults_and_unknown_keys_survive_a_save()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json");
        File.WriteAllText(path, """{ "playSound": "yes", "imageFormat": 12, "futureKey": { "a": 1 } }""");
        var p = new Preferences(path);
        Assert.True(p.PlaySound);
        Assert.Equal("png", p.ImageFormat);
        p.OpenEditor = true;
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(1, (int?)json["futureKey"]?["a"]);
        Assert.Equal(true, (bool?)json["openEditor"]);
    }

    [Fact]
    public void Changed_fires_once_per_real_change_with_the_property_name()
    {
        using var tmp = new TempDir();
        var p = new Preferences(tmp.File("settings.json"));
        var names = new List<string>();
        p.Changed += names.Add;
        p.Theme = "light";
        p.Theme = "light";
        p.RecordFps = 24;
        Assert.Equal(new[] { "Theme", "RecordFps" }, names);
    }

    [Fact]
    public void Custom_values_round_trip()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json");
        new Preferences(path).SetCustom("winFlag", 42);
        var q = new Preferences(path);
        Assert.Equal(42, q.GetCustom("winFlag", 0));
        Assert.Equal("d", q.GetCustom("missing", "d"));
        Assert.Equal(0, q.GetCustom("imageFormat", 0)); // wrong type -> fallback
    }

    [Fact]
    public void Concurrent_writes_leave_a_valid_file()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json");
        var p = new Preferences(path);
        Parallel.For(0, 200, i => { p.RecordFps = i + 1; p.Theme = i % 2 == 0 ? "light" : "dark"; });
        var q = new Preferences(path);
        Assert.Equal(p.RecordFps, q.RecordFps);
        Assert.Equal(p.Theme, q.Theme);
    }

    [Fact]
    public void NewCapturePath_uses_the_French_stamp_and_dedups()
    {
        using var tmp = new TempDir();
        var dir = tmp.File("shots");
        var p = new Preferences(tmp.File("settings.json")) { SaveDirectory = dir };
        p.Now = () => new DateTime(2026, 11, 16, 16, 37, 5);

        var first = p.NewCapturePath("png");
        Assert.Equal(Path.Combine(dir, "FrameLine 2026-11-16 à 16.37.05.png"), first);
        Assert.True(Directory.Exists(dir));
        File.WriteAllText(first, "");

        var second = p.NewCapturePath(".png");
        Assert.Equal(Path.Combine(dir, "FrameLine 2026-11-16 à 16.37.05 (2).png"), second);
        File.WriteAllText(second, "");
        Assert.Equal(Path.Combine(dir, "FrameLine 2026-11-16 à 16.37.05 (3).png"), p.NewCapturePath("png"));
        // Other extension: no collision.
        Assert.Equal(Path.Combine(dir, "FrameLine 2026-11-16 à 16.37.05.mp4"), p.NewCapturePath("mp4"));
    }

    [Fact]
    public void Unusable_save_directory_falls_back_to_the_default()
    {
        using var tmp = new TempDir();
        var blocker = tmp.File("file-not-folder");
        File.WriteAllText(blocker, "x");
        var p = new Preferences(tmp.File("settings.json")) { SaveDirectory = Path.Combine(blocker, "sub") };
        Assert.Equal(Preferences.DefaultSaveDirectory, p.EnsureSaveDirectory());
    }
}

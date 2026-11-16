using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace FrameLine.Library;

/// Theme-dependent brushes of the library window, as "Lib.*" resources on the window (set with
/// SetResourceReference, swapped when the app theme changes). Values follow the macOS views:
/// sidebar grey white 0.17 / 0.95, detail charcoal white 0.075 / 0.98, and the white-alpha
/// overlays of the dark look mirrored to black-alpha for the light one.
internal static class LibraryTheme
{
    public static void Apply(ResourceDictionary rd, bool dark)
    {
        foreach (var (k, v) in Palette(dark)) rd[k] = v;
    }

    private static IEnumerable<(string, Brush)> Palette(bool dark)
    {
        Brush Ov(double a) => dark ? Glyphs.White(a) : Glyphs.Black(a);
        yield return ("Lib.SidebarBg", Glyphs.Gray(dark ? 0.17 : 0.95));
        yield return ("Lib.DetailBg", Glyphs.Gray(dark ? 0.075 : 0.98));
        yield return ("Lib.Text", dark ? Brushes.White : Glyphs.Gray(0.1));
        yield return ("Lib.TextSecondary", dark ? Glyphs.White(0.55) : Glyphs.Black(0.5));
        yield return ("Lib.Hover", Ov(0.05));
        yield return ("Lib.SelectedStroke", dark ? Glyphs.White(0.85) : Glyphs.Black(0.7));
        yield return ("Lib.Badge", Ov(0.12));
        yield return ("Lib.ToolHover", Ov(0.10));
        yield return ("Lib.ThumbStroke", Ov(0.08));
        yield return ("Lib.Divider", Ov(0.10));
        yield return ("Lib.TileBg", Ov(0.06));
        yield return ("Lib.TileStroke", Ov(0.05));
        yield return ("Lib.PillBg", Ov(0.07));
        yield return ("Lib.PillBgHover", Ov(0.13));
        yield return ("Lib.PillStroke", Ov(0.14));
        yield return ("Lib.KeyBg", Ov(0.10));
        yield return ("Lib.KeyStroke", Ov(0.28));
        yield return ("Lib.PillHiBg", dark ? Brushes.White : Glyphs.Gray(0.12));
        yield return ("Lib.PillHiBgHover", dark ? Glyphs.White(0.92) : Glyphs.Gray(0.2));
        yield return ("Lib.PillHiText", dark ? Brushes.Black : Brushes.White);
        yield return ("Lib.PillHiKeyBg", dark ? Glyphs.Black(0.06) : Glyphs.White(0.12));
        yield return ("Lib.PillHiKeyStroke", dark ? Glyphs.Black(0.14) : Glyphs.White(0.3));
        yield return ("Lib.DeleteBg", Ov(0.13));
        yield return ("Lib.DeleteBgHover", Ov(0.22));
        yield return ("Lib.DeleteStroke", Ov(0.14));
        yield return ("Lib.DeleteStrokeHover", Ov(0.24));
        yield return ("Lib.Accent", Glyphs.Brush(0x0A, 0x84, 0xFF, 1));
    }
}

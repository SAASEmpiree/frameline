using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using FrameLine.Core;
using Microsoft.Win32;

namespace FrameLine.Platform;

/// Port of ThemeManager.swift: applies Preferences.Theme ("system" | "light" | "dark", default
/// dark) to the whole app by swapping the FL.* brushes of Theme.xaml. "system" follows the Windows
/// app mode (Settings > Personalisation > Colours) live.
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static bool _listening;
    private static readonly List<WeakReference<Window>> Attached = new();

    /// True when the effective app theme is dark.
    public static bool IsDark { get; private set; } = true;

    /// Raised on the UI thread after the palette changed (theme setting or Windows app mode).
    public static event Action? Changed;
    /// Raised on the UI thread when the Windows app mode or taskbar colour changes.
    public static event Action? SystemThemeChanged;

    /// Windows "Default app mode" (AppsUseLightTheme = 0 means dark).
    public static bool SystemIsDark => ReadDword("AppsUseLightTheme", 1) == 0;

    /// Windows "Default Windows mode": colour of the taskbar / notification area.
    public static bool TaskbarIsLight => ReadDword("SystemUsesLightTheme", 0) == 1;

    public static void Apply()
    {
        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        IsDark = Preferences.Shared.Theme switch
        {
            "light" => false,
            "dark" => true,
            _ => SystemIsDark,
        };
        var app = Application.Current;
        if (app == null) return;
        foreach (var (key, color) in Palette(IsDark))
            app.Resources[key] = Frozen(color);
        foreach (var weak in Attached.ToArray())
        {
            if (weak.TryGetTarget(out var w)) Glass.SetDarkTitleBar(w, IsDark);
            else Attached.Remove(weak);
        }
        Changed?.Invoke();
    }

    /// Keeps a window's title bar (caption buttons) in the app theme, now and on every change.
    public static void Attach(Window window)
    {
        Glass.SetDarkTitleBar(window, IsDark);
        Attached.Add(new WeakReference<Window>(window));
    }

    /// The FL.* palette for one appearance, for windows that follow another appearance than the app
    /// (the Settings window follows the SYSTEM appearance, like the macOS app).
    public static ResourceDictionary CreatePalette(bool dark)
    {
        var d = new ResourceDictionary();
        foreach (var (key, color) in Palette(dark)) d[key] = Frozen(color);
        return d;
    }

    private static IEnumerable<(string, Color)> Palette(bool dark)
    {
        if (dark)
        {
            yield return ("FL.WindowBackground", Hex(0x1E1E1E));
            yield return ("FL.SurfaceBackground", Hex(0x282828));
            yield return ("FL.SurfaceBorder", Hex(0x3A3A3A));
            yield return ("FL.ControlBackground", Hex(0x363636));
            yield return ("FL.ControlHoverBackground", Hex(0x424242));
            yield return ("FL.ControlBorder", Hex(0x4A4A4A));
            yield return ("FL.TextPrimary", Hex(0xF2F2F2));
            yield return ("FL.TextSecondary", Hex(0x9A9A9A));
            yield return ("FL.TextTertiary", Hex(0x6E6E6E));
            yield return ("FL.Separator", Hex(0x3A3A3A));
            yield return ("FL.MenuBackground", Hex(0x2B2B2B));
            yield return ("FL.MenuHover", Hex(0x0A84FF));
            yield return ("FL.Accent", Hex(0x0A84FF));
            yield return ("FL.AccentText", Hex(0xFFFFFF));
            yield return ("FL.SwitchOffTrack", Hex(0x4A4A4A));
        }
        else
        {
            yield return ("FL.WindowBackground", Hex(0xF3F3F3));
            yield return ("FL.SurfaceBackground", Hex(0xFFFFFF));
            yield return ("FL.SurfaceBorder", Hex(0xE0E0E0));
            yield return ("FL.ControlBackground", Hex(0xFFFFFF));
            yield return ("FL.ControlHoverBackground", Hex(0xEDEDED));
            yield return ("FL.ControlBorder", Hex(0xD0D0D0));
            yield return ("FL.TextPrimary", Hex(0x1A1A1A));
            yield return ("FL.TextSecondary", Hex(0x6E6E6E));
            yield return ("FL.TextTertiary", Hex(0xA0A0A0));
            yield return ("FL.Separator", Hex(0xE3E3E3));
            yield return ("FL.MenuBackground", Hex(0xF9F9F9));
            yield return ("FL.MenuHover", Hex(0x007AFF));
            yield return ("FL.Accent", Hex(0x007AFF));
            yield return ("FL.AccentText", Hex(0xFFFFFF));
            yield return ("FL.SwitchOffTrack", Hex(0xD6D6D6));
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Theme switches arrive as General (ImmersiveColorSet); SystemEvents raises on its own thread.
        if (e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Preferences.Shared.Theme == "system") Apply();
            SystemThemeChanged?.Invoke();
        });
    }

    private static int ReadDword(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }

    private static Color Hex(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FrameLine.Shell;

/// Content of the four setup-guide steps and their small components (keycap, pill button, tiles).
/// The English copy is the macOS copy (it was not localised there either), adapted only where the
/// platform differs: system tray instead of menu bar, PC instead of Mac, and the screen-recording
/// permission step replaced by the optional microphone access (Windows needs no capture permission).
internal sealed class OnboardingSteps
{
    public static readonly Brush WindowBackground = Solid(0x12, 0x12, 0x12);   // ≈ #121212 near-black
    public static readonly Brush CardFill = Solid(0x20, 0x20, 0x20);
    public static readonly Brush CardStroke = Solid(0x3F, 0x3F, 0x3F);
    private static readonly Brush RaisedFill = Solid(0x28, 0x28, 0x28);
    private static readonly Brush RaisedStroke = Solid(0x46, 0x46, 0x46);
    private static readonly Brush OkGreen = Solid(0x6B, 0xD4, 0x6A);
    private static readonly Brush Orange = Solid(0xFF, 0x9F, 0x0A);
    private static readonly Brush Secondary = White(0.55);
    private static readonly Brush Dim = White(0.46);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly Action _onStartCapture;
    private readonly Action _onFinish;
    private bool _hasPermission = MicrophoneAccess.IsAllowed;
    private int _builtStep = -1;
    private Action? _rebuild;

    public OnboardingSteps(Action onStartCapture, Action onFinish)
    {
        _onStartCapture = onStartCapture;
        _onFinish = onFinish;
    }

    public FrameworkElement Build(int step)
    {
        _builtStep = step;
        var content = step switch
        {
            0 => Welcome(),
            1 => Permission(),
            2 => Shortcuts(),
            _ => Ready(),
        };
        return content;
    }

    /// Polled every second: redraws the permission-dependent step when the state flips.
    public void RefreshPermission()
    {
        bool now = MicrophoneAccess.IsAllowed;
        if (now == _hasPermission) return;
        _hasPermission = now;
        if (_builtStep is 1 or 3) _rebuild?.Invoke();
    }

    /// The window installs this so a permission flip can swap the card content in place.
    public void SetRebuild(Action rebuild) => _rebuild = rebuild;

    // MARK: - Step 1 — Welcome

    private FrameworkElement Welcome()
    {
        var s = Stack(18);
        s.Children.Add(BrandMark(58));
        s.Children.Add(Heading("Welcome to FrameLine",
            "Capture, annotate and ship beautiful screenshots — right from your system tray. Two quick steps and you're ready."));
        var features = Stack(18);
        features.Margin = new Thickness(0, 4, 0, 0);
        features.Children.Add(FeatureRow("\uE722", "Pixel-perfect captures", "Area, window and fullscreen — with a frozen-screen selector."));
        features.Children.Add(FeatureRow("\uE70F", "Annotate anything", "Arrows, shapes, text, counters and redaction, all non-destructive."));
        features.Children.Add(FeatureRow("\uE734", "Beautify and share", "Backgrounds, padding and one-click copy for posts and docs."));
        s.Children.Add(features);
        return s;
    }

    // MARK: - Step 2 — Microphone (optional): the Windows counterpart of "Allow Screen Recording"

    private FrameworkElement Permission()
    {
        var s = Stack(18);
        s.Children.Add(IconTile("\uE720"));
        s.Children.Add(Heading("Microphone (optional)",
            "Windows lets FrameLine capture your screen without any permission. Allow the microphone only if you want your voice in screen recordings. Everything stays on your PC."));

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Glyph("\uE720", 16, _hasPermission ? OkGreen : White(0.9)));
        var label = new TextBlock { Text = "Microphone", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        FrameworkElement status;
        if (_hasPermission) status = GrantedBadge();
        else
        {
            var enable = Pill("Enable", filled: false, glyph: "\uE72A");
            enable.Click += (_, _) => MicrophoneAccess.OpenSettings();
            status = enable;
        }
        Grid.SetColumn(status, 2);
        row.Children.Add(status);
        s.Children.Add(Raised(row, new Thickness(14, 12, 14, 12)));

        if (_hasPermission)
        {
            s.Children.Add(new TextBlock { Text = "You're all set — FrameLine can use your microphone.", FontSize = 13, Foreground = Secondary });
        }
        else
        {
            var open = new Button { Content = IconLabel("\uE713", Tr.L("open_windows_settings", "Open Windows Settings")), Style = (Style)Application.Current.FindResource("FL.Button"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) => MicrophoneAccess.OpenSettings();
            s.Children.Add(open);
        }
        return s;
    }

    // MARK: - Step 3 — Shortcuts

    private FrameworkElement Shortcuts()
    {
        var s = Stack(18);
        s.Children.Add(IconTile("\uE765"));
        s.Children.Add(Heading("Set up your shortcuts",
            "FrameLine answers the classic screenshot keys out of the box. Change them anytime in Settings → Hotkeys."));
        var rows = Stack(12);
        rows.Margin = new Thickness(0, 2, 0, 0);
        rows.Children.Add(ShortcutRow("\uE7A8", "Capture Area", CaptureHotkey.Region));
        rows.Children.Add(ShortcutRow("\uE922", "Capture Window", CaptureHotkey.Window));
        rows.Children.Add(ShortcutRow("\uE740", "Capture Fullscreen", CaptureHotkey.FullScreen));
        s.Children.Add(rows);

        string region = CaptureHotkey.Label(CaptureHotkey.Region);
        bool ours = !HotKeyManager.Shared.Conflicts.Contains(region);
        s.Children.Add(CheckLine(ours, ours
            ? $"Shortcuts ready — {region} now belongs to FrameLine."
            : $"{region} is already used by another app.", 12.5));
        return s;
    }

    // MARK: - Step 4 — All set

    private FrameworkElement Ready()
    {
        var s = Stack(18);
        s.Children.Add(IconTile("\uE73E"));
        s.Children.Add(Heading("You're all set", "Grab your first capture now — it lands in the library, ready to annotate."));

        var checks = Stack(9);
        checks.Children.Add(CheckLine(_hasPermission, _hasPermission ? "Microphone allowed" : "Microphone not allowed (optional)"));
        checks.Children.Add(CheckLine(true, $"{CaptureHotkey.Label(CaptureHotkey.Region)} captures an area"));
        s.Children.Add(checks);

        var boxes = Stack(12);
        var trayInfo = new DockPanel();
        var mark = BrandMark(22);
        DockPanel.SetDock(mark, Dock.Left);
        trayInfo.Children.Add(mark);
        trayInfo.Children.Add(new TextBlock
        {
            Text = "This is FrameLine in your system tray — click it anytime for captures, your library and settings.",
            FontSize = 12.5, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        boxes.Children.Add(Raised(trayInfo, new Thickness(13)));

        var login = new Grid();
        login.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        login.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        login.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        login.Children.Add(Glyph("\uE7E8", 16, White(0.9)));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = "Launch FrameLine at login", FontSize = 13, FontWeight = FontWeights.SemiBold });
        texts.Children.Add(new TextBlock { Text = "Your shortcuts are ready the moment your PC starts.", FontSize = 11.5, Foreground = Secondary, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(texts, 1);
        login.Children.Add(texts);
        var toggle = new CheckBox { IsChecked = LaunchAtLogin.IsEnabled, Style = (Style)Application.Current.FindResource("FL.ToggleSwitch"), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        void Apply(bool on)
        {
            LaunchAtLogin.IsEnabled = on;
            bool real = LaunchAtLogin.IsEnabled;             // reflect the real state
            if (toggle.IsChecked != real) toggle.IsChecked = real;
        }
        toggle.Checked += (_, _) => Apply(true);
        toggle.Unchecked += (_, _) => Apply(false);
        Grid.SetColumn(toggle, 2);
        login.Children.Add(toggle);
        boxes.Children.Add(Raised(login, new Thickness(13)));
        s.Children.Add(boxes);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var first = Pill("Take Your First Screenshot", filled: true, glyph: "\uE722");
        first.Click += (_, _) => _onStartCapture();
        actions.Children.Add(first);
        var library = PlainButton("Open Library", 13);
        library.Margin = new Thickness(20, 0, 0, 0);
        library.Click += (_, _) => _onFinish();
        actions.Children.Add(library);
        s.Children.Add(actions);
        return s;
    }

    // MARK: - Components

    public static Image BrandMark(double size)
    {
        var img = new Image { Width = size, Height = size, Source = SettingsPanes.AppIconImage((int)Math.Ceiling(size * 3)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    /// White tile with a dark glyph (BridgeShot step icons).
    private static FrameworkElement IconTile(string glyph) => new Border
    {
        Width = 64, Height = 64, CornerRadius = new CornerRadius(18), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left,
        Child = Glyph(glyph, 28, Solid(0x1F, 0x1F, 0x1F), center: true),
    };

    private static FrameworkElement Heading(string title, string text)
    {
        var s = Stack(8);
        s.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.Bold });
        s.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        return s;
    }

    private static FrameworkElement FeatureRow(string glyph, string title, string subtitle)
    {
        var row = new DockPanel();
        var icon = Glyph(glyph, 16, White(0.92));
        icon.Width = 20;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 12, 0);
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var texts = Stack(2);
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(texts);
        return row;
    }

    private static FrameworkElement ShortcutRow(string glyph, string title, uint vk)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Glyph(glyph, 15, White(0.9)));
        var label = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var k in CaptureHotkey.Keys(vk)) keys.Children.Add(Keycap(k));
        Grid.SetColumn(keys, 2);
        row.Children.Add(keys);
        return Raised(row, new Thickness(14, 5, 14, 5));
    }

    private static FrameworkElement Keycap(string label) => new Border
    {
        MinWidth = 20, MinHeight = 18, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 0, 0),
        CornerRadius = new CornerRadius(5), Background = White(0.05), BorderBrush = White(0.06), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private static FrameworkElement CheckLine(bool ok, string text, double size = 13)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Glyph(ok ? "\uE73E" : "\uE7BA", 14, ok ? OkGreen : Orange));
        row.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static FrameworkElement GrantedBadge()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = OkGreen, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "Granted", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = OkGreen, Margin = new Thickness(5, 0, 0, 0) });
        var green45 = OkGreen.Clone();
        green45.Opacity = 0.45;
        green45.Freeze();
        return new Border
        {
            Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(12), Background = White(0.05),
            BorderBrush = green45, BorderThickness = new Thickness(1), Child = content, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Border Raised(UIElement child, Thickness padding) => new()
    {
        Padding = padding, CornerRadius = new CornerRadius(10), Background = RaisedFill, BorderBrush = RaisedStroke,
        BorderThickness = new Thickness(1), Child = child,
    };

    /// White filled (dark text) or translucent outlined capsule, with a leading glyph.
    public static Button Pill(string title, bool filled, string glyph)
    {
        var face = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 6, 16, 6),
            Background = filled ? White(0.97) : White(0.10),
            BorderBrush = filled ? Brushes.Transparent : White(0.18), BorderThickness = new Thickness(filled ? 0 : 1),
            Child = IconLabel(glyph, title, filled ? Solid(0x0F, 0x0F, 0x0F) : Brushes.White, 11, 12, bold: true),
        };
        var b = new Button { Content = face, Cursor = System.Windows.Input.Cursors.Hand, Focusable = true, Template = BareTemplate() };
        b.MouseEnter += (_, _) => face.Background = filled ? Brushes.White : White(0.16);
        b.MouseLeave += (_, _) => face.Background = filled ? White(0.97) : White(0.10);
        return b;
    }

    /// Text-only button in the secondary colour ("Skip Setup", "‹ Back", "Open Library").
    public static Button PlainButton(string title, double size)
    {
        var text = new TextBlock { Text = title, FontSize = size, FontWeight = FontWeights.Medium, Foreground = Secondary };
        var b = new Button { Content = text, Cursor = System.Windows.Input.Cursors.Hand, Template = BareTemplate(), VerticalAlignment = VerticalAlignment.Center };
        b.MouseEnter += (_, _) => text.Foreground = Brushes.White;
        b.MouseLeave += (_, _) => text.Foreground = Secondary;
        return b;
    }

    private static ControlTemplate BareTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var bg = new FrameworkElementFactory(typeof(Border));
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bg.AppendChild(presenter);
        t.VisualTree = bg;
        return t;
    }

    private static FrameworkElement IconLabel(string glyph, string text, Brush? fg = null, double glyphSize = 12, double textSize = 12, bool bold = false)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        var g = Glyph(glyph, glyphSize, fg);
        g.Margin = new Thickness(0, 0, 7, 0);
        s.Children.Add(g);
        var t = new TextBlock { Text = text, FontSize = textSize, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        if (fg != null) t.Foreground = fg;
        s.Children.Add(t);
        return s;
    }

    private static TextBlock Glyph(string glyph, double size, Brush? fg, bool center = false)
    {
        var t = new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        };
        if (fg != null) t.Foreground = fg;
        return t;
    }

    private static StackPanel Stack(double spacing) => new SpacedStack(spacing);

    public static Brush White(double alpha) => Solid(0xFF, 0xFF, 0xFF, alpha);

    private static Brush Solid(byte r, byte g, byte b, double alpha = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), r, g, b));
        brush.Freeze();
        return brush;
    }

    /// SwiftUI VStack(spacing:): every child after the first gets the gap added to its top margin
    /// when it is added (margins are set before adding).
    private sealed class SpacedStack : StackPanel
    {
        private readonly double _spacing;
        public SpacedStack(double spacing) => _spacing = spacing;

        protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
        {
            base.OnVisualChildrenChanged(visualAdded, visualRemoved);
            if (visualAdded is FrameworkElement fe && InternalChildren.Count > 1)
            {
                var m = fe.Margin;
                fe.Margin = new Thickness(m.Left, m.Top + _spacing, m.Right, m.Bottom);
            }
        }
    }
}

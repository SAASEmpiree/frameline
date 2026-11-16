using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using FrameLine.Platform;
using FrameLine.Shell;

namespace FrameLine.Library;

/// The library window (MainWindowController.swift): sidebar of captures + detail, under a merged
/// title bar that carries the sidebar toggle, the three capture buttons aligned on the detail's
/// left edge (they slide left with the divider when the sidebar collapses) and the "FrameLine"
/// title, like the unified NSToolbar with a transparent title bar.
internal sealed class LibraryWindow : Window
{
    private static LibraryWindow? _instance;

    private const double TitleH = 44, SidebarDefault = 292, SidebarMin = 260, SidebarMax = 400;
    private const double ToggleLeft = 10, ToggleSize = 30;

    private readonly Border _root;
    private readonly Grid _grid;
    private readonly ColumnDefinition _sideCol;
    private readonly ColumnDefinition _dividerCol;
    private readonly Border _sideBackground;
    private readonly FrameworkElement _sidebarHost;
    private readonly GridSplitter _splitter;
    private readonly Border _spacer;
    private readonly SidebarView _sidebar;
    private readonly DetailView _detail;
    private readonly CaptionButton _maxButton, _minButton, _closeButton;
    private readonly FlatIconButton _toggle, _region, _window, _full;
    private bool _collapsed;
    private int _sidebarAnimGen;
    private double _expandedWidth = SidebarDefault;
    private LibNative.RECT? _lastNormal;

    public static void ShowWindow(string? selectPath)
    {
        if (selectPath != null) LibraryModel.Shared.SelectOnly(selectPath);
        var w = _instance ??= new LibraryWindow();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        // Pull it in front even when another app is active (tray / hotkey invocation).
        w.Topmost = true;
        w.Topmost = false;
        w.Activate();
        w._sidebar.List.Focus();
        LibraryModel.Shared.RequestReload();   // CaptureLibrary.refresh() on every show
    }

    public static void Toggle()
    {
        if (_instance is { IsVisible: true } w) w.Close(); else ShowWindow(null);
    }

    public static bool IsOpen => _instance is { IsVisible: true };

    private LibraryWindow()
    {
        Title = "FrameLine";
        Width = 1080;
        Height = 700;
        MinWidth = 860;
        MinHeight = 540;
        FontFamily = Glyphs.UiFont;
        ResizeMode = ResizeMode.CanResize;
        UseLayoutRounding = true;
        LibraryTheme.Apply(Resources, ThemeManager.IsDark);
        this.SetResourceReference(BackgroundProperty, "Lib.DetailBg");
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/frameline-256.png")); } catch { }

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleH,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),   // keeps the DWM shadow / Win11 corners
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _grid = new Grid();
        _sideCol = new ColumnDefinition { Width = new GridLength(SidebarDefault), MinWidth = SidebarMin, MaxWidth = SidebarMax };
        _dividerCol = new ColumnDefinition { Width = new GridLength(1) };
        _grid.ColumnDefinitions.Add(_sideCol);
        _grid.ColumnDefinitions.Add(_dividerCol);
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
        _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleH) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Column backgrounds run under the title bar: grey on the sidebar side, charcoal on the detail.
        _sideBackground = new Border { ClipToBounds = true }.Bg("Lib.SidebarBg");
        Place(_sideBackground, 0, 0, rowSpan: 2);
        var divider = new Border().Bg("Lib.Divider");
        Place(divider, 1, 0, rowSpan: 2);
        Place(new Border().Bg("Lib.DetailBg"), 2, 0, rowSpan: 2);

        _sidebar = new SidebarView();
        _sidebarHost = DropImport.Wrap(_sidebar);
        _sidebarHost.ClipToBounds = true;
        Place(_sidebarHost, 0, 1);
        _detail = new DetailView();
        var detailHost = DropImport.Wrap(_detail);
        Place(detailHost, 2, 1);

        _splitter = new GridSplitter
        {
            Width = 7,
            Margin = new Thickness(-3, 0, -3, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Columns,
            Background = Brushes.Transparent,
            Focusable = false,
        };
        WindowChrome.SetIsHitTestVisibleInChrome(_splitter, true);
        Panel.SetZIndex(_splitter, 10);
        Place(_splitter, 1, 0, rowSpan: 2);
        _splitter.DragCompleted += (_, _) => { if (!_collapsed) _expandedWidth = _sideCol.ActualWidth; };

        // Title bar content.
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var toggle = _toggle = new FlatIconButton(Glyphs.Sidebar, LibText.T("lib_toggle_sidebar"), 15, ToggleSize) { Margin = new Thickness(ToggleLeft, 0, 0, 0) };
        toggle.Click += ToggleSidebar;
        left.Children.Add(toggle);
        _spacer = new Border { Width = 0 };
        left.Children.Add(_spacer);
        var coord = AppCoordinator.Shared;
        var region = _region = new FlatIconButton(Glyphs.Region, LibText.T("m_capture_area"), 15, ToggleSize);
        region.Click += coord.CaptureRegion;
        var window = _window = new FlatIconButton("window", LibText.T("m_capture_window"), 15, ToggleSize) { Margin = new Thickness(4, 0, 0, 0) };
        window.Click += coord.CaptureWindow;
        var full = _full = new FlatIconButton(Glyphs.Monitor, LibText.T("m_capture_fullscreen"), 15, ToggleSize) { Margin = new Thickness(4, 0, 0, 0) };
        full.Click += coord.CaptureFull;
        left.Children.Add(region);
        left.Children.Add(window);
        left.Children.Add(full);
        var title = Ui.Text("FrameLine", 14, FontWeights.Bold);
        title.Margin = new Thickness(12, 0, 0, 0);
        left.Children.Add(title);

        var caption = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var min = _minButton = new CaptionButton(Glyphs.ChromeMinimize, LibText.T("lib_minimize"), false, TitleH);
        min.Click += () => SystemCommands.MinimizeWindow(this);
        _maxButton = new CaptionButton(Glyphs.ChromeMaximize, LibText.T("lib_maximize"), false, TitleH);
        _maxButton.Click += () =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        };
        var close = _closeButton = new CaptionButton(Glyphs.ChromeClose, LibText.T("lib_close"), true, TitleH);
        close.Click += Close;
        caption.Children.Add(min);
        caption.Children.Add(_maxButton);
        caption.Children.Add(close);

        var titleBar = new Grid();
        titleBar.Children.Add(left);
        titleBar.Children.Add(caption);
        Panel.SetZIndex(titleBar, 5);
        Place(titleBar, 0, 0, colSpan: 3);

        _sideBackground.SizeChanged += (_, _) => SyncSpacer();

        _root = new Border { Child = _grid };
        Content = _root;

        ThemeManager.Attach(this);
        ThemeManager.Changed += OnThemeChanged;
        FrameLine.Core.I18n.Shared.LanguageChanged += OnLanguageChanged;
        LibraryModel.Shared.AddClockUser();

        SourceInitialized += (_, _) => RestorePlacement();
        StateChanged += (_, _) => OnStateChanged();
        LocationChanged += (_, _) => RememberNormal();
        SizeChanged += (_, _) => RememberNormal();
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            FrameLine.Core.I18n.Shared.LanguageChanged -= OnLanguageChanged;
            LibraryModel.Shared.RemoveClockUser();
            _sidebar.Detach();
            _detail.Detach();
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    private void Place(UIElement e, int col, int row, int rowSpan = 1, int colSpan = 1)
    {
        Grid.SetColumn(e, col);
        Grid.SetRow(e, row);
        Grid.SetRowSpan(e, rowSpan);
        Grid.SetColumnSpan(e, colSpan);
        _grid.Children.Add(e);
    }

    private void OnThemeChanged()
    {
        LibraryTheme.Apply(Resources, ThemeManager.IsDark);
    }

    /// Raised on the thread that changed the language: relabel on ours.
    private void OnLanguageChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded && !IsVisible) return;
        _toggle.Tip = LibText.T("lib_toggle_sidebar");
        _region.Tip = LibText.T("m_capture_area");
        _window.Tip = LibText.T("m_capture_window");
        _full.Tip = LibText.T("m_capture_fullscreen");
        _minButton.SetGlyph(Glyphs.ChromeMinimize, LibText.T("lib_minimize"));
        _closeButton.SetGlyph(Glyphs.ChromeClose, LibText.T("lib_close"));
        bool max = WindowState == WindowState.Maximized;
        _maxButton.SetGlyph(max ? Glyphs.ChromeRestore : Glyphs.ChromeMaximize, LibText.T(max ? "lib_restore" : "lib_maximize"));
        _sidebar.Relabel();
        _detail.Relabel();
        foreach (var e in LibraryModel.Shared.Items) e.RaiseSubtitles();   // "5 min. ago"
    });

    /// The capture buttons start at the detail's left edge while the sidebar is open, and group
    /// next to the toggle when it is collapsed (the spacer follows the divider frame by frame).
    private void SyncSpacer()
    {
        double dividerX = _sideBackground.ActualWidth + _dividerCol.ActualWidth;
        _spacer.Width = Math.Max(0, dividerX + 10 - (ToggleLeft + ToggleSize));
    }

    private void ToggleSidebar()
    {
        bool collapse = !_collapsed;
        if (collapse) _expandedWidth = _sideCol.ActualWidth;
        _collapsed = collapse;
        _sideCol.MinWidth = 0;
        _splitter.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        double from = _sideCol.ActualWidth, to = collapse ? 0 : Math.Clamp(_expandedWidth, SidebarMin, SidebarMax);
        if (!collapse) { _sidebarHost.Visibility = Visibility.Visible; _dividerCol.Width = new GridLength(1); }
        int gen = ++_sidebarAnimGen;
        var clock = Stopwatch.StartNew();
        const double duration = 0.25;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            if (gen != _sidebarAnimGen) { CompositionTarget.Rendering -= tick; return; }   // superseded mid-flight
            double t = Math.Min(1, clock.Elapsed.TotalSeconds / duration);
            double v = from + (to - from) * CubicBezier.EaseInOut.Evaluate(t);
            _sideCol.Width = new GridLength(Math.Max(0, v));
            if (t < 1) return;
            CompositionTarget.Rendering -= tick;
            if (collapse)
            {
                _sidebarHost.Visibility = Visibility.Collapsed;
                _dividerCol.Width = new GridLength(0);
            }
            else _sideCol.MinWidth = SidebarMin;
            SyncSpacer();
        };
        CompositionTarget.Rendering += tick;
    }

    private void OnStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        // A maximized WindowChrome window hangs off the monitor by its resize frame: inset the content.
        double inset = max ? LibMetrics.MaximizedOverhangDip(new WindowInteropHelper(this).Handle) : 0;
        _root.Margin = new Thickness(inset);
        _maxButton.SetGlyph(max ? Glyphs.ChromeRestore : Glyphs.ChromeMaximize, LibText.T(max ? "lib_restore" : "lib_maximize"));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        // Ctrl+Backspace (Cmd+Delete on macOS) = selection to the Recycle Bin, never while typing
        // text (an editor text field uses it to delete a word).
        if (ctrl && key == Key.Back)
        {
            if (Keyboard.FocusedElement is TextBoxBase) return;
            LibraryModel.Shared.TrashSelection();
            e.Handled = true;
        }
        else if (ctrl && key == Key.OemComma)
        {
            Dispatcher.BeginInvoke(() => AppCoordinator.Shared.OpenPreferences());
            e.Handled = true;
        }
        else if (ctrl && key == Key.W)
        {
            Close();
            e.Handled = true;
        }
    }

    // MARK: - Placement memory (setFrameAutosaveName "FrameLineMain")

    private sealed record Saved(int X, int Y, int W, int H, bool Max, double Sidebar);

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameLine", "library-window.json");

    private void RememberNormal()
    {
        if (WindowState != WindowState.Normal || !IsLoaded) return;
        if (WindowMotion.TryGetRect(this, out var r)) _lastNormal = r;
    }

    private void RestorePlacement()
    {
        Saved? s = null;
        try { if (File.Exists(StatePath)) s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(StatePath)); }
        catch { }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (s != null && s.W > 200 && s.H > 200)
        {
            var rect = new LibNative.RECT { Left = s.X, Top = s.Y, Right = s.X + s.W, Bottom = s.Y + s.H };
            if (LibNative.MonitorFromRect(ref rect, LibNative.MONITOR_DEFAULTTONULL) != IntPtr.Zero)
            {
                // Move first (DPI change of the target monitor), then apply the exact saved rect.
                LibNative.SetWindowPos(hwnd, IntPtr.Zero, s.X, s.Y, 0, 0, LibNative.SWP_NOSIZE | LibNative.SWP_NOZORDER | LibNative.SWP_NOACTIVATE);
                LibNative.SetWindowPos(hwnd, IntPtr.Zero, s.X, s.Y, s.W, s.H, LibNative.SWP_NOZORDER | LibNative.SWP_NOACTIVATE);
                if (s.Sidebar >= SidebarMin && s.Sidebar <= SidebarMax)
                {
                    _sideCol.Width = new GridLength(s.Sidebar);
                    _expandedWidth = s.Sidebar;
                }
                if (s.Max) WindowState = WindowState.Maximized;
                return;
            }
        }
        this.CenterOnActiveScreen();   // first opening: centred on the active monitor
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal) RememberNormal();
        if (_lastNormal is not { } r) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var side = _collapsed ? _expandedWidth : _sideCol.ActualWidth;
            File.WriteAllText(StatePath, JsonSerializer.Serialize(
                new Saved(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized, side)));
        }
        catch { }
    }
}

/// Minimize / maximize / close of the merged title bar (Windows 11 caption metrics, 46 px wide).
internal sealed class CaptionButton : Border
{
    private readonly TextBlock _glyph;
    private readonly bool _isClose;
    public event Action? Click;

    public CaptionButton(string glyph, string tip, bool isClose, double height)
    {
        _isClose = isClose;
        Width = 46;
        Height = height;
        Background = Brushes.Transparent;
        ToolTip = tip;
        _glyph = new TextBlock
        {
            Text = glyph, FontFamily = Glyphs.IconFont, FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Lib.Text");
        Child = _glyph;
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);
        MouseEnter += (_, _) =>
        {
            if (_isClose) { Background = Glyphs.Brush(0xC4, 0x2B, 0x1C, 1); _glyph.Foreground = Brushes.White; }
            else SetResourceReference(BackgroundProperty, "Lib.ToolHover");
        };
        MouseLeave += (_, _) =>
        {
            Background = Brushes.Transparent;
            _glyph.SetResourceReference(TextBlock.ForegroundProperty, "Lib.Text");
        };
        MouseLeftButtonDown += (_, e) => { e.Handled = true; CaptureMouse(); };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            bool inside = Ui.Inside(this, e);
            ReleaseMouseCapture();
            if (inside) Click?.Invoke();
        };
    }

    public void SetGlyph(string glyph, string tip)
    {
        _glyph.Text = glyph;
        ToolTip = tip;
    }
}

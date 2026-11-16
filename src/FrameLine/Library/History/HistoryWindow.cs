using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FrameLine.Core;
using FrameLine.Shell;

namespace FrameLine.Library;

/// "Capture History" (CaptureHistoryPanel.swift): a floating panel anchored to the RIGHT edge of
/// the active monitor stacking the 12 most recent captures (thumbnail + name + "5 min. ago ·
/// W×H"). Closes on Esc and on any click outside it, in this app or another one.
internal sealed class HistoryWindow : Window
{
    private static HistoryWindow? _shared;
    public static HistoryWindow Shared => _shared ??= new HistoryWindow();

    private const double PanelW = 312, PanelH = 520, Shadow = 14, ScreenMargin = 14;
    private const int MaxCards = 12;

    private readonly TextBlock _count;
    private readonly Grid _body;
    private readonly StackPanel _list;
    private readonly ScrollViewer _scroll;
    private readonly FrameworkElement _empty;
    private readonly List<(CaptureEntry Entry, PropertyChangedEventHandler Handler)> _subs = new();
    private readonly DispatcherTimer _outsideClick;
    private int _generation;
    private DateTime _hiddenByOutsideAt = DateTime.MinValue;
    private bool _wasButtonDown;

    private HistoryWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = PanelW + Shadow * 2;
        Height = PanelH + Shadow * 2;
        Title = LibText.T("m_capture_history");
        FontFamily = Glyphs.UiFont;
        Foreground = Brushes.White;

        var header = new DockPanel { Margin = new Thickness(14, 13, 14, 10), LastChildFill = false };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Glyphs.Icon(Glyphs.History, 13, Brushes.White, bold: true));
        title.Children.Add(new TextBlock
        {
            Text = LibText.T("m_capture_history"), FontSize = 14, FontWeight = FontWeights.Bold,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);
        _count = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border
        {
            Background = Glyphs.White(0.12), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 2, 7, 2), Child = _count, VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);

        _list = new StackPanel { Margin = new Thickness(10) };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
            Focusable = false,
        };
        _empty = EmptyState();
        _body = new Grid();
        _body.Children.Add(_scroll);
        _body.Children.Add(new OverlayScrollThumb(_scroll) { HorizontalAlignment = HorizontalAlignment.Right });
        _body.Children.Add(_empty);

        var stack = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        stack.Children.Add(header);
        var divider = new Border { Height = 1, Background = Glyphs.White(0.10) };
        DockPanel.SetDock(divider, Dock.Top);
        stack.Children.Add(divider);
        stack.Children.Add(_body);

        Content = new Border
        {
            Margin = new Thickness(Shadow),
            CornerRadius = new CornerRadius(16),
            // .ultraThinMaterial in the dark appearance, as a translucent charcoal.
            Background = Glyphs.Brush(0x24, 0x24, 0x26, 0.94),
            BorderBrush = Glyphs.White(0.12),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.45 },
            Child = new Border
            {
                CornerRadius = new CornerRadius(16),
                ClipToBounds = true,
                Child = stack,
            },
        };

        Deactivated += (_, _) => { if (IsVisible) HideFromOutside(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; HidePanel(); } };
        // Clicks in OTHER apps when the panel could not take the foreground (opened from a hotkey,
        // Windows refuses SetForegroundWindow): watch the buttons like macOS's global monitor.
        _outsideClick = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => PollOutsideClick(), Dispatcher);
        Action onItems = () => { if (IsVisible) Rebuild(); };
        LibraryModel.Shared.ItemsChanged += onItems;
        // Alt+F4 or app shutdown: forget this instance, the next toggle builds a fresh one.
        Closed += (_, _) =>
        {
            _outsideClick.Stop();
            LibraryModel.Shared.ItemsChanged -= onItems;
            Unsubscribe();
            SetClock(false);
            if (ReferenceEquals(_shared, this)) _shared = null;
        };
    }

    public static void HideIfOpen() => _shared?.HidePanel();

    public void Toggle()
    {
        if (IsVisible && Opacity > 0.01) { HidePanel(); return; }
        // The click that opened the tray menu already closed us: do not reopen on the same gesture.
        if ((DateTime.UtcNow - _hiddenByOutsideAt).TotalMilliseconds < 300) return;
        ShowPanel();
    }

    public void ShowPanel()
    {
        _generation++;
        Rebuild();
        SetClock(true);
        var m = MonitorRef.OfForegroundWindow();
        int x = m.Work.Right - m.Px(PanelW) - m.Px(ScreenMargin) - m.Px(Shadow);
        int y = m.Work.Top + m.Px(ScreenMargin) - m.Px(Shadow);
        Opacity = 0;
        WindowMotion.MoveTo(this, x, y);
        Show();
        Activate();
        WindowMotion.Animate(this, x, y, 1, 0.16, CubicBezier.EaseOut);
        _wasButtonDown = LibInput.AnyMouseButtonDown();
        _outsideClick.Start();
    }

    public void HidePanel()
    {
        _outsideClick.Stop();
        if (!IsVisible) return;
        int gen = ++_generation;
        SetClock(false);
        WindowMotion.TryGetRect(this, out var r);
        WindowMotion.Animate(this, r.Left, r.Top, 0, 0.12, CubicBezier.EaseOut, () =>
        {
            if (gen != _generation) return;    // shown again during the fade
            Hide();
            Unsubscribe();
        });
    }

    private bool _clockHeld;
    private void SetClock(bool on)
    {
        if (on == _clockHeld) return;
        _clockHeld = on;
        if (on) LibraryModel.Shared.AddClockUser(); else LibraryModel.Shared.RemoveClockUser();
    }

    private void HideFromOutside()
    {
        _hiddenByOutsideAt = DateTime.UtcNow;
        HidePanel();
    }

    private void PollOutsideClick()
    {
        bool down = LibInput.AnyMouseButtonDown();
        if (down && !_wasButtonDown && !IsMouseOver && !IsActive)
        {
            if (LibNative.GetCursorPos(out var p) && WindowMotion.TryGetRect(this, out var r)
                && (p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom))
                HideFromOutside();
        }
        _wasButtonDown = down;
    }

    private void Rebuild()
    {
        Unsubscribe();
        _list.Children.Clear();
        var items = LibraryModel.Shared.Items;
        _count.Text = items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        bool empty = items.Count == 0;
        _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        foreach (var e in items.Take(MaxCards))
        {
            var card = new HistoryCard(e, () =>
            {
                AppCoordinator.Shared.OpenInApp(e.Path);
                HidePanel();
            });
            if (_list.Children.Count > 0) card.Margin = new Thickness(0, 7, 0, 0);
            _list.Children.Add(card);
            PropertyChangedEventHandler h = (_, a) => card.Refresh(a.PropertyName);
            e.PropertyChanged += h;
            _subs.Add((e, h));
            e.RequestFit(Dispatcher);
        }
    }

    private void Unsubscribe()
    {
        foreach (var (e, h) in _subs) e.PropertyChanged -= h;
        _subs.Clear();
    }

    private static FrameworkElement EmptyState()
    {
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        s.Children.Add(Glyphs.Icon(Glyphs.History, 30, Glyphs.White(0.55)));
        s.Children.Add(new TextBlock
        {
            Text = LibText.T("lib_history_empty"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0),
        });
        s.Children.Add(new TextBlock
        {
            Text = LibText.T("lib_history_empty_sub"), FontSize = 11, Foreground = Glyphs.White(0.55),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 8, 20, 0),
        });
        return s;
    }
}

/// One history row: 78×52 thumbnail (whole capture, letterboxed, NOT cropped), name, subtitle.
internal sealed class HistoryCard : Border
{
    private readonly CaptureEntry _entry;
    private readonly Image _thumb;
    private readonly FrameworkElement? _placeholder;
    private readonly TextBlock _subtitle;

    public HistoryCard(CaptureEntry entry, Action open)
    {
        _entry = entry;
        Padding = new Thickness(7);
        CornerRadius = new CornerRadius(11);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        SetHover(false);
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        MouseLeftButtonUp += (_, e) => { e.Handled = true; open(); };

        var thumbGrid = new Grid
        {
            Width = 78, Height = 52,
            Background = Glyphs.Black(0.35),
            Clip = new RectangleGeometry(new Rect(0, 0, 78, 52), 8, 8),
        };
        _thumb = new Image { Stretch = Stretch.Uniform, Source = entry.Fit };
        RenderOptions.SetBitmapScalingMode(_thumb, BitmapScalingMode.HighQuality);
        thumbGrid.Children.Add(_thumb);
        if (!entry.IsVideo)
        {
            _placeholder = Glyphs.Icon(Glyphs.Photo, 18, Glyphs.White(0.5));
            _placeholder.Visibility = entry.Fit == null ? Visibility.Visible : Visibility.Collapsed;
            thumbGrid.Children.Add(_placeholder);
        }
        else thumbGrid.Children.Add(VideoBadge.Create(28));
        var thumbFrame = new Grid();
        thumbFrame.Children.Add(thumbGrid);
        thumbFrame.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = Glyphs.White(0.10),
            IsHitTestVisible = false,
        });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        texts.Children.Add(new MiddleEllipsisText { Text = entry.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        _subtitle = new TextBlock
        {
            Text = entry.SubtitleEnglish, FontSize = 10.5, Foreground = Glyphs.White(0.55),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
        };
        texts.Children.Add(_subtitle);

        var row = new DockPanel();
        DockPanel.SetDock(thumbFrame, Dock.Left);
        row.Children.Add(thumbFrame);
        row.Children.Add(texts);
        Child = row;
    }

    private void SetHover(bool on)
    {
        Background = Glyphs.White(on ? 0.10 : 0.04);
        BorderBrush = Glyphs.White(on ? 0.18 : 0.07);
    }

    public void Refresh(string? property)
    {
        if (property is nameof(CaptureEntry.Fit) or null)
        {
            _thumb.Source = _entry.Fit;
            if (_placeholder != null) _placeholder.Visibility = _entry.Fit == null ? Visibility.Visible : Visibility.Collapsed;
            if (_entry.Fit == null) _entry.RequestFit(Dispatcher);
        }
        if (property is nameof(CaptureEntry.SubtitleEnglish) or null) _subtitle.Text = _entry.SubtitleEnglish;
    }
}

using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FrameLine.Core;
using FrameLine.Shell;

namespace FrameLine.Library;

internal enum ThumbAction { Open, Annotate, Copy, Enhance, Pin, Explorer, Trash, Close }

/// One floating post-capture card (ThumbnailPanel + ThumbnailRootView of CaptureThumbnail.swift):
/// a borderless, non-activating, topmost window that never takes the focus from the user's app.
/// The window never resizes: the hover zoom scales the IMAGE only, inside a transparent margin,
/// so the close button and the action capsule stay exactly on their hit areas.
internal sealed class ThumbnailPanel : Window
{
    public const double CardWidth = 240, CardHeight = 170, Pad = 10;
    private const double BarMargin = 10, BarHeight = 36, ChipWidth = 29, InnerPad = 6;
    private const double DragThreshold = 4;

    public string FilePath { get; }
    public bool IsVideo { get; }
    public bool IsDraggingFile { get; private set; }
    public Action? ClosedCallback;

    private BitmapSource? _image;        // what the card shows (may be a reduced decode)
    private readonly BitmapSource? _full; // full in-memory capture, when the caller had it
    private readonly Image _imageView;
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly Border _bar;
    private readonly TranslateTransform _barShift = new();
    private readonly RimButton _close;
    private readonly IconChip[] _chips;
    private readonly Grid _card;
    private Point? _pressOrigin;
    private bool _isDismissing, _isSuspended, _closed;

    public ThumbnailPanel(BitmapSource? image, BitmapSource? full, string path, bool isVideo)
    {
        FilePath = path;
        IsVideo = isVideo;
        _image = image;
        _full = full;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Width = CardWidth + Pad * 2;
        Height = CardHeight + Pad * 2;
        Title = "FrameLine";
        SourceInitialized += (_, _) =>
            LibNative.AddExStyle(new WindowInteropHelper(this).Handle, LibNative.WS_EX_NOACTIVATE | LibNative.WS_EX_TOOLWINDOW);
        Closed += (_, _) => _closed = true;

        var root = new Grid();
        // Shadow of the card (hasShadow on macOS), drawn in the transparent margin.
        root.Children.Add(new Border
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Black,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.45 },
            IsHitTestVisible = false,
        });

        _card = new Grid
        {
            Margin = new Thickness(Pad),
            Width = CardWidth,
            Height = CardHeight,
            Clip = new RectangleGeometry(new Rect(0, 0, CardWidth, CardHeight), 12, 12),
            Background = Brushes.Black,
        };
        root.Children.Add(_card);

        var imageHost = new Grid
        {
            Background = Brushes.Black,
            RenderTransform = _zoom,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _imageView = new Image
        {
            Source = image,
            Stretch = Stretch.UniformToFill,          // cover: fills the card, overflow clipped
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(_imageView, BitmapScalingMode.HighQuality);
        imageHost.Children.Add(_imageView);
        if (isVideo) imageHost.Children.Add(PlayDisc());   // child of the image: follows the zoom
        _card.Children.Add(imageHost);

        // Action capsule: video files only get open / Explorer / trash (annotate, copy, enhance and
        // pin make no sense on an .mp4); the file can still be dragged out.
        var defs = isVideo
            ? new (string Glyph, string Tip, ThumbAction Action, bool Red)[]
            {
                (Glyphs.Play, LibText.T("lib_open"), ThumbAction.Open, false),
                (Glyphs.Folder, LibText.T("lib_show_in_explorer"), ThumbAction.Explorer, false),
                (Glyphs.Delete, LibText.T("lib_move_to_trash"), ThumbAction.Trash, true),
            }
            : new (string Glyph, string Tip, ThumbAction Action, bool Red)[]
            {
                (Glyphs.Edit, LibText.T("lib_annotate"), ThumbAction.Annotate, false),
                (Glyphs.Copy, LibText.T("lib_copy"), ThumbAction.Copy, false),
                ("sparkles", LibText.T("lib_enhance"), ThumbAction.Enhance, false),
                (Glyphs.Pin, LibText.T("lib_pin"), ThumbAction.Pin, false),
                (Glyphs.Folder, LibText.T("lib_show_in_explorer"), ThumbAction.Explorer, false),
                (Glyphs.Delete, LibText.T("lib_move_to_trash"), ThumbAction.Trash, true),
            };
        var chipsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(InnerPad, 0, InnerPad, 0) };
        _chips = new IconChip[defs.Length];
        for (int i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            var chip = new IconChip(d.Glyph, d.Tip,
                d.Red ? Glyphs.Brush(0xFF, 0x45, 0x3A, 0.32) : Glyphs.White(0.20),
                d.Red ? Glyphs.SystemRed : Brushes.White, 12, ChipWidth, BarHeight);
            var action = d.Action;
            chip.Click += () => Handle(action);
            _chips[i] = chip;
            chipsPanel.Children.Add(chip);
        }
        _bar = new Border
        {
            Height = BarHeight,
            Width = defs.Length * ChipWidth + InnerPad * 2,
            CornerRadius = new CornerRadius(BarHeight / 2),
            // hudWindow material (dark appearance) + the extra black .55 layer of the macOS capsule.
            Background = Glyphs.Brush(0x10, 0x10, 0x10, 0.88),
            BorderBrush = Glyphs.White(0.28),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, BarMargin),
            RenderTransform = _barShift,
            Opacity = 0,
            IsHitTestVisible = false,                  // hidden: clicks reach the card ("open")
            Child = chipsPanel,
        };
        _bar.MouseLeftButtonDown += (_, e) => e.Handled = true;   // gaps between chips never "open"
        _bar.MouseLeftButtonUp += (_, e) => e.Handled = true;
        _card.Children.Add(_bar);

        _close = new RimButton(Glyphs.Close, LibText.T("lib_close"), 20, 9,
            RimButton.C(0, 0, 0, 0.72), RimButton.C(0, 0, 0, 0.55),
            RimButton.C(255, 255, 255, 0.28), RimButton.C(255, 255, 255, 0.6), bold: true)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _close.Click += () => Handle(ThumbAction.Close);
        _card.Children.Add(_close);

        // Hairline rim over everything.
        root.Children.Add(new Border
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Glyphs.White(0.18),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        });

        _card.MouseEnter += (_, _) => SetHover(true);
        _card.MouseLeave += (_, _) => { if (!IsDraggingFile) SetHover(false); };
        _card.MouseLeftButtonDown += OnCardDown;
        _card.MouseMove += OnCardMove;
        _card.MouseLeftButtonUp += OnCardUp;
        _card.LostMouseCapture += (_, _) => { if (!IsDraggingFile) _pressOrigin = null; };

        Content = root;
    }

    public void SetImage(BitmapSource image)
    {
        _image = image;
        _imageView.Source = image;
    }

    private static FrameworkElement PlayDisc()
    {
        // play.circle.fill: white disc with the triangle cut out, soft shadow.
        var g = new Grid
        {
            Width = 44, Height = 44, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.45 },
        };
        g.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Glyphs.White(0.95) });
        g.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Glyphs.PlayTriangle, Fill = Glyphs.Black(0.55), Stretch = Stretch.Uniform,
            Width = 15, Height = 18, Margin = new Thickness(3, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    // MARK: - Presentation

    /// Slides in from the nearest horizontal edge (off-screen) with a fade, BridgeShot style.
    public void Appear(int x, int y, bool fromRight, MonitorRef monitor)
    {
        WindowMotion.Handle(this);
        if (_isSuspended)
        {
            // Created during a capture session: placed, but only shown when the session ends.
            WindowMotion.MoveTo(this, x, y);
            Opacity = 1;
            return;
        }
        int w = monitor.Px(Width);
        int off = fromRight ? w + monitor.Px(28) : -(w + monitor.Px(28));
        Opacity = 0;
        WindowMotion.MoveTo(this, x + off, y);
        Show();
        WindowMotion.Animate(this, x, y, 1, 0.34, CubicBezier.EaseOut);
    }

    public void Relocate(int x, int y, bool animated)
    {
        if (_isDismissing) return;
        if (WindowMotion.TryGetRect(this, out var r) && r.Left == x && r.Top == y && !WindowMotion.IsAnimating(this)) return;
        if (!animated || _isSuspended || !IsVisible) { WindowMotion.MoveTo(this, x, y); if (!_isSuspended) Opacity = 1; return; }
        WindowMotion.Animate(this, x, y, 1, 0.3, CubicBezier.EaseInOut);
    }

    public void DismissImmediately() => Dismiss(false);

    /// Off screen for the duration of a capture (instantly, no fade), back at the same place after.
    public void SetSuspended(bool suspended)
    {
        if (_isSuspended == suspended) return;
        if (_isDismissing)
        {
            // Fading out after close/trash: it must still leave the screen if a capture starts,
            // or the half-transparent card is burnt into the screenshot. Never shown again.
            if (suspended) { _isSuspended = true; if (!_closed) Hide(); }
            return;
        }
        _isSuspended = suspended;
        if (suspended)
        {
            SetImageZoom(false, instant: true);
            SetChromeVisible(false, instant: true);
            if (WindowMotion.TryGetRect(this, out var r)) WindowMotion.MoveTo(this, r.Left, r.Top); // ends a slide
            Hide();
        }
        else
        {
            Opacity = 1;
            Show();
        }
    }

    private void Dismiss(bool animated)
    {
        if (_isDismissing) return;
        _isDismissing = true;
        if (!animated || !IsVisible) { FinishClose(); return; }
        WindowMotion.TryGetRect(this, out var r);
        WindowMotion.Animate(this, r.Left, r.Top, 0, 0.16, CubicBezier.EaseIn, FinishClose);
    }

    private void FinishClose()
    {
        if (_closed) return;
        WindowMotion.Stop(this);
        var cb = ClosedCallback;
        ClosedCallback = null;
        cb?.Invoke();
        // A drag session started from this card may still be running (DoDragDrop is a nested
        // loop): closing then would destroy the drag source under OLE. Close once it ends.
        if (IsDraggingFile) { Hide(); _closeAfterDrag = true; return; }
        Close();
    }

    private bool _closeAfterDrag;

    // MARK: - Hover

    private void SetHover(bool inside)
    {
        SetImageZoom(inside);
        SetChromeVisible(inside);
    }

    private void SetImageZoom(bool on, bool instant = false)
    {
        double to = on ? 1.04 : 1.0;
        if (instant)
        {
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _zoom.ScaleX = _zoom.ScaleY = to;
            return;
        }
        var a = new DoubleAnimation(to, TimeSpan.FromSeconds(0.28)) { EasingFunction = CubicBezier.EaseOut };
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void SetChromeVisible(bool visible, bool instant = false)
    {
        // Hidden chrome is transparent to clicks so a click on the card opens it.
        _bar.IsHitTestVisible = visible;
        _close.IsHitTestVisible = visible;
        if (!visible) foreach (var c in _chips) c.ResetHover();
        if (instant)
        {
            _bar.BeginAnimation(OpacityProperty, null);
            _close.BeginAnimation(OpacityProperty, null);
            _barShift.BeginAnimation(TranslateTransform.YProperty, null);
            _bar.Opacity = _close.Opacity = visible ? 1 : 0;
            _barShift.Y = 0;
            return;
        }
        var d = TimeSpan.FromSeconds(0.26);
        // Very soft reveal: the capsule rises 5 px while fading in, the close button only fades.
        if (visible) _barShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, d) { EasingFunction = CubicBezier.EaseOut });
        _bar.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, d) { EasingFunction = CubicBezier.EaseOut });
        _close.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, d) { EasingFunction = CubicBezier.EaseOut });
    }

    // MARK: - Click vs drag

    private void OnCardDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(_card);      // wait: a drag beyond the threshold, or a release?
        _card.CaptureMouse();
        e.Handled = true;
    }

    private void OnCardMove(object sender, MouseEventArgs e)
    {
        if (_pressOrigin is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_card);
        if (Math.Abs(p.X - start.X) <= DragThreshold && Math.Abs(p.Y - start.Y) <= DragThreshold
            && (p - start).Length <= DragThreshold) return;
        _pressOrigin = null;                       // now a drag: the button-up will not open
        _card.ReleaseMouseCapture();
        BeginFileDrag();
    }

    private void OnCardUp(object sender, MouseButtonEventArgs e)
    {
        bool click = _pressOrigin != null;
        _pressOrigin = null;
        _card.ReleaseMouseCapture();
        if (click) Handle(ThumbAction.Open);       // a plain click on the card opens it in the app
    }

    /// Drags the captured FILE out (Explorer, Discord, Drive, Mail...), always as a copy. Falls
    /// back to the bitmap alone when the file is not on disk (yet, or any more).
    private void BeginFileDrag()
    {
        var data = new DataObject();
        bool exists = File.Exists(FilePath);
        if (exists) data.SetFileDropList(new StringCollection { FilePath });
        if (!IsVideo)
        {
            var bmp = _full ?? (exists ? LibImages.LoadFull(FilePath) : null) ?? _image;
            if (bmp != null) data.SetImage(bmp);
            else if (!exists) return;
        }
        else if (!exists) return;

        IsDraggingFile = true;
        DragDropEffects result;
        try { result = DragDrop.DoDragDrop(_card, data, DragDropEffects.Copy); }
        catch { result = DragDropEffects.None; }
        IsDraggingFile = false;
        _pressOrigin = null;
        if (_closeAfterDrag) { Close(); return; }
        if (!_card.IsMouseOver) SetHover(false);
        // "Close the preview after drag and drop": only when a target ACCEPTED the drop; a drag
        // released in the void or cancelled leaves the card in place.
        if (result != DragDropEffects.None && Preferences.Shared.ClosePreviewAfterDrag) Handle(ThumbAction.Close);
    }

    // MARK: - Actions -> AppCoordinator

    private void Handle(ThumbAction action)
    {
        var path = FilePath;
        var coord = AppCoordinator.Shared;
        switch (action)
        {
            case ThumbAction.Close:
                Dismiss(true);                                  // only the cross closes explicitly
                break;
            case ThumbAction.Open:
                coord.OpenInApp(path);                          // main window, this capture selected
                break;
            case ThumbAction.Annotate:
            case ThumbAction.Enhance:                           // "Enhance" leads to the editor for now
                coord.Annotate(path);
                break;
            case ThumbAction.Copy:
                // Region/window captures show the card BEFORE the file is written (encoding runs
                // in the background): copy the in-memory image then instead of an empty clipboard.
                if (File.Exists(path)) coord.CopyImage(path);
                else if ((_full ?? _image) is { } img && LibClipboard.CopyImage(img)) LibToast.Show(LibText.T("lib_copied"));
                break;
            case ThumbAction.Pin:
                coord.PinToScreen(path);
                break;
            case ThumbAction.Explorer:
                coord.RevealInExplorer(path);
                break;
            case ThumbAction.Trash:
                coord.Trash(path);
                Dismiss(true);                                  // file gone: the card goes too
                break;
        }
    }
}

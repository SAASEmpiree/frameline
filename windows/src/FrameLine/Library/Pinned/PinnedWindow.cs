using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace FrameLine.Library;

/// A capture floating above every window (PinnedShotWindow.swift), for "Pin to Screen" and
/// "Restore to Screen": borderless, rounded, shadowed, dragged from anywhere, with close (top
/// left) and copy (top right) revealed on hover. Windows additions: wheel = zoom around the
/// cursor, Ctrl+wheel = opacity, Esc = close, right-click menu with the same actions.
internal sealed class PinnedWindow : Window
{
    private static readonly List<PinnedWindow> Live = new();
    private const double Radius = 10, Shadow = 16, MinZoom = 0.1, MaxZoom = 4;

    private readonly BitmapSource _image;
    private readonly string? _path;
    private readonly double _naturalW, _naturalH;  // DIP size at zoom 1
    private readonly Grid _imageGrid;
    private readonly FrameworkElement[] _controls;
    private double _zoom = 1;

    public static void ShowFile(string path)
    {
        var dispatcher = Application.Current.Dispatcher;
        Task.Run(() => LibImages.LoadFull(path)).ContinueWith(t =>
        {
            var img = t.IsCompletedSuccessfully ? t.Result : null;
            if (img != null) dispatcher.BeginInvoke(() => ShowImage(img, path));
        });
    }

    public static void ShowImage(BitmapSource image, string? path)
    {
        var m = MonitorRef.UnderCursor();
        var w = new PinnedWindow(image, path, m);
        int step = m.Px(Live.Count * 24);              // one more pin = one more cascade step
        Live.Add(w);
        int pw = m.Px(w.Width), ph = m.Px(w.Height);
        int x = m.Work.Left + (m.Work.Width - pw) / 2 + step;
        int y = m.Work.Top + (m.Work.Height - ph) / 2 + step;
        WindowMotion.MoveTo(w, x, y);
        w.Show();
        w.Activate();
    }

    private PinnedWindow(BitmapSource image, string? path, MonitorRef m)
    {
        _image = image;
        _path = path;
        // Shown at 1 image pixel = 1 screen pixel, shrunk to 70 % of the monitor if larger, ratio
        // kept, never enlarged (a small capture stays sharp).
        double pxW = Math.Max(1, image.PixelWidth), pxH = Math.Max(1, image.PixelHeight);
        double maxW = m.Work.Width * 0.7, maxH = m.Work.Height * 0.7;
        double fit = Math.Min(1, Math.Min(maxW / pxW, maxH / pxH));
        _naturalW = Math.Round(pxW * fit) / m.Scale;
        _naturalH = Math.Round(pxH * fit) / m.Scale;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "FrameLine";
        FontFamily = Glyphs.UiFont;
        Width = _naturalW + Shadow * 2;
        Height = _naturalH + Shadow * 2;

        var root = new Grid { Margin = new Thickness(Shadow), Background = Brushes.Transparent };
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Radius), Background = Brushes.Black, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.45 },
        });
        _imageGrid = new Grid();
        var img = new Image { Source = image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        _imageGrid.Children.Add(img);
        _imageGrid.SizeChanged += (_, e) =>
            _imageGrid.Clip = new RectangleGeometry(new Rect(e.NewSize), Radius, Radius);
        root.Children.Add(_imageGrid);
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Radius), BorderBrush = Glyphs.White(0.15),
            BorderThickness = new Thickness(0.5), IsHitTestVisible = false,
        });

        var close = MakeButton(Glyphs.Close, LibText.T("lib_close"), HorizontalAlignment.Left);
        close.Click += Close;
        var copy = MakeButton(Glyphs.Copy, LibText.T("lib_copy"), HorizontalAlignment.Right);
        copy.Click += Copy;
        root.Children.Add(close);
        root.Children.Add(copy);
        _controls = new FrameworkElement[] { close, copy };

        root.MouseEnter += (_, _) => SetControls(true);
        root.MouseLeave += (_, _) => SetControls(false);
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }   // background = move handle
        };
        root.MouseWheel += OnWheel;
        root.ContextMenu = BuildMenu();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Copy(); }
        };
        Closed += (_, _) => Live.Remove(this);
        Content = root;
    }

    private static RimButton MakeButton(string glyph, string tip, HorizontalAlignment side) =>
        new(glyph, tip, 22, 10, RimButton.C(0, 0, 0, 0.55), RimButton.C(0, 0, 0, 0.7),
            RimButton.C(255, 255, 255, 0), RimButton.C(255, 255, 255, 0.3))
        {
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Opacity = 0,
        };

    private void SetControls(bool visible)
    {
        foreach (var c in _controls)
            c.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromSeconds(0.12)));
    }

    /// PNG files go out byte for byte (lossless); anything else as a re-encoded PNG + bitmap.
    private void Copy()
    {
        byte[]? png = null;
        if (_path != null && string.Equals(Path.GetExtension(_path), ".png", StringComparison.OrdinalIgnoreCase))
        {
            try { png = File.ReadAllBytes(_path); } catch { }
        }
        LibClipboard.CopyImage(_image, png);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        double notches = e.Delta / 120.0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetOpacity(Opacity + 0.1 * notches);
            return;
        }
        SetZoom(_zoom * Math.Pow(1.1, notches), e.GetPosition(this));
    }

    private void SetOpacity(double value)
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = Math.Clamp(value, 0.2, 1);
    }

    private void SetZoom(double zoom, Point? anchor = null)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double oldW = Width, oldH = Height;
        double newW = _naturalW * zoom + Shadow * 2, newH = _naturalH * zoom + Shadow * 2;
        var p = anchor ?? new Point(oldW / 2, oldH / 2);
        // Keep the point under the cursor fixed.
        double left = Left + p.X - p.X * (newW / oldW);
        double top = Top + p.Y - p.Y * (newH / oldH);
        _zoom = zoom;
        Width = newW;
        Height = newH;
        Left = left;
        Top = top;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.SetResourceReference(StyleProperty, "FL.ContextMenu");
        var copy = new MenuItem { Header = LibText.T("lib_copy") };
        copy.Click += (_, _) => Copy();
        menu.Items.Add(copy);
        var actual = new MenuItem { Header = LibText.T("lib_reset_zoom") };
        actual.Click += (_, _) => SetZoom(1);
        menu.Items.Add(actual);
        var opacity = new MenuItem { Header = LibText.T("lib_opacity") };
        foreach (var pct in new[] { 100, 80, 60, 40 })
        {
            var item = new MenuItem { Header = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:P0}", pct / 100.0) };
            item.Click += (_, _) => SetOpacity(pct / 100.0);
            opacity.Items.Add(item);
        }
        menu.Items.Add(opacity);
        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = LibText.T("lib_close") };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);
        return menu;
    }
}

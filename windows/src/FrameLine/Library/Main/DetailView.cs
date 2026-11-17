using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FrameLine.Recording;
using FrameLine.Shell;

namespace FrameLine.Library;

/// Right column (DetailView.swift): the focused capture, or the empty state with the three
/// capture pills. macOS embeds the annotation editor / video trimmer here; the Windows contract
/// only opens them as windows, so the detail shows a viewer with an action capsule unless the
/// editor modules register an embeddable view (MainLibraryWindow.ImageDetailFactory /
/// VideoDetailFactory).
internal sealed class DetailView : Grid
{
    private readonly Grid _host = new();
    private string? _shownPath;
    private DateTime _shownCreated;
    private FrameworkElement? _content;
    private MediaElement? _media;
    private bool _playing;
    private int _loadToken;

    public DetailView()
    {
        this.Bg("Lib.DetailBg");
        Children.Add(_host);
        LibraryModel.Shared.SelectionChanged += Update;
        LibraryModel.Shared.ItemsChanged += Update;
        LibraryModel.Shared.BeforeTrash += OnBeforeTrash;
        Update();
    }

    public void Detach()
    {
        LibraryModel.Shared.SelectionChanged -= Update;
        LibraryModel.Shared.ItemsChanged -= Update;
        LibraryModel.Shared.BeforeTrash -= OnBeforeTrash;
        Teardown(immediate: true);
    }

    /// The display language changed: rebuild the built-in viewer / empty state with the new
    /// strings. An embedded editor is left alone (it keeps its undo history and owns its texts).
    public void Relabel()
    {
        if (_embedded != null) return;
        Teardown(immediate: false);
        _shownPath = null;
        Update();
    }

    private void OnBeforeTrash(IReadOnlyList<string> paths)
    {
        if (_shownPath == null) return;
        foreach (var p in paths)
            if (string.Equals(p, _shownPath, StringComparison.OrdinalIgnoreCase)) { Teardown(immediate: true); _shownPath = null; return; }
    }

    private void Update()
    {
        var model = LibraryModel.Shared;
        var e = model.FocusedEntry;
        if (e == null)
        {
            if (_shownPath == null && _content != null) { UpdateEmptyTexts(); return; }
            Teardown(immediate: false);
            _shownPath = null;
            // Captures exist but none is selected (Ctrl+click on the last selected card): never
            // show "No captures" next to a full list.
            _content = EmptyState();
            _host.Children.Add(_content);
            return;
        }
        if (model.IsTrashing(e.Path))
        {
            // Being recycled: reopening it here would take its handle back before the move.
            Teardown(immediate: true);
            _shownPath = null;
            return;
        }
        bool samePath = string.Equals(_shownPath, e.Path, StringComparison.OrdinalIgnoreCase);
        // An embedded editor saving in place changes the date: keep it (and its undo history);
        // only the built-in viewer reloads the new pixels.
        if (samePath && (_shownCreated == e.Created || _embedded != null)) return;
        Teardown(immediate: false);
        _shownPath = e.Path;
        _shownCreated = e.Created;
        _content = Build(e);
        _host.Children.Add(_content);
    }

    private TextBlock? _emptyTitle, _emptySub;
    private void UpdateEmptyTexts()
    {
        bool has = LibraryModel.Shared.Items.Count > 0;
        if (_emptyTitle != null) _emptyTitle.Text = LibText.T(has ? "lib_no_capture_selected" : "lib_no_captures");
        if (_emptySub != null) _emptySub.Text = LibText.T(has ? "lib_detail_unselected_sub" : "lib_detail_empty_sub");
    }

    /// The previous view is dismantled when the selection changes; an embedded editor gets
    /// Dispose() one dispatcher turn later (macOS saves the annotations there, deferred so the
    /// library is not modified in the middle of the update).
    private void Teardown(bool immediate)
    {
        _loadToken++;
        if (_media != null)
        {
            try { _media.Stop(); _media.Close(); } catch { }
            _media.Source = null;
            _media = null;
            _playing = false;
        }
        if (_content != null)
        {
            _host.Children.Remove(_content);
            if (_embedded is IDisposable d)
            {
                if (immediate) d.Dispose();
                else Dispatcher.BeginInvoke(DispatcherPriority.Background, () => d.Dispose());
            }
            _embedded = null;
            _content = null;
        }
        _emptyTitle = _emptySub = null;
    }

    private FrameworkElement? _embedded;

    private FrameworkElement Build(CaptureEntry e)
    {
        var factory = e.IsVideo ? MainLibraryWindow.VideoDetailFactory : MainLibraryWindow.ImageDetailFactory;
        if (factory != null)
        {
            try
            {
                if (factory(e.Path) is { } custom) { _embedded = custom; return custom; }
            }
            catch { }
        }
        return e.IsVideo ? BuildVideo(e) : BuildImage(e);
    }

    // MARK: - Image

    private FrameworkElement BuildImage(CaptureEntry e)
    {
        var root = new Grid();
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(28, 28, 28, 84),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.35 };
        root.Children.Add(image);

        int token = _loadToken;
        string path = e.Path;
        Task.Run(() => LibImages.LoadFull(path)).ContinueWith(t =>
            Dispatcher.BeginInvoke(() =>
            {
                if (token != _loadToken || !t.IsCompletedSuccessfully || t.Result == null) return;
                var img = t.Result;
                image.Source = img;
                // Pixels, not the file's DPI: a 144-dpi capture must not shrink to half size.
                var dpi = VisualTreeHelper.GetDpi(this);
                image.MaxWidth = img.PixelWidth / dpi.DpiScaleX;
                image.MaxHeight = img.PixelHeight / dpi.DpiScaleY;
            }));

        var coord = AppCoordinator.Shared;
        root.Children.Add(Capsule(
            (Glyphs.Edit, LibText.T("lib_annotate"), () => coord.Annotate(path), false),
            (Glyphs.Copy, LibText.T("lib_copy_image"), () => coord.CopyImage(path), false),
            (Glyphs.Text, LibText.T("lib_copy_text"), () => coord.CopyText(path), false),
            (Glyphs.Pin, LibText.T("lib_pin_to_screen"), () => Dispatcher.BeginInvoke(() => coord.PinToScreen(path)), false),
            (Glyphs.Folder, LibText.T("lib_show_in_explorer"), () => coord.RevealInExplorer(path), false),
            (Glyphs.Share, LibText.T("lib_export"), () => coord.ExportCapture(path), false),
            (Glyphs.Delete, LibText.T("lib_move_to_trash"), () => LibraryModel.Shared.TrashOne(path), true)));
        root.MouseLeftButtonDown += (_, ev) =>
        {
            if (ev.ClickCount == 2) { coord.Annotate(path); ev.Handled = true; }   // double-click = edit
        };
        return root;
    }

    // MARK: - Video

    private FrameworkElement BuildVideo(CaptureEntry e)
    {
        var root = new Grid();
        string path = e.Path;
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(28, 28, 28, 84),
            Source = new Uri(path, UriKind.Absolute),
        };
        _media = media;
        var playBadge = VideoBadge.Create(64);
        media.MediaOpened += (_, _) => { if (!_playing) { media.Play(); media.Pause(); } };  // first frame
        media.MediaEnded += (_, _) => { media.Stop(); _playing = false; playBadge.Visibility = Visibility.Visible; };
        root.Children.Add(media);
        root.Children.Add(playBadge);

        void Toggle()
        {
            if (_media != media) return;
            if (_playing) media.Pause(); else media.Play();
            _playing = !_playing;
            playBadge.Visibility = _playing ? Visibility.Collapsed : Visibility.Visible;
        }
        media.MouseLeftButtonUp += (_, _) => Toggle();
        playBadge.IsHitTestVisible = true;
        playBadge.MouseLeftButtonUp += (_, _) => Toggle();

        var coord = AppCoordinator.Shared;
        root.Children.Add(Capsule(
            (Glyphs.Play, LibText.T("lib_play_pause"), Toggle, false),
            (Glyphs.Cut, LibText.T("lib_trim"), () =>
            {
                if (_media == media) { media.Stop(); _playing = false; playBadge.Visibility = Visibility.Visible; }
                VideoTrimmer.Open(path);
            }, false),
            (Glyphs.Folder, LibText.T("lib_show_in_explorer"), () => coord.RevealInExplorer(path), false),
            (Glyphs.Share, LibText.T("lib_export"), () => coord.ExportCapture(path), false),
            (Glyphs.Delete, LibText.T("lib_move_to_trash"), () => LibraryModel.Shared.TrashOne(path), true)));
        return root;
    }

    /// Floating dark capsule of actions at the bottom centre (the editor's capsule look).
    private static FrameworkElement Capsule(params (string Glyph, string Tip, Action Action, bool Red)[] defs)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
        foreach (var d in defs)
        {
            var chip = new IconChip(d.Glyph, d.Tip,
                d.Red ? Glyphs.Brush(0xFF, 0x45, 0x3A, 0.32) : Glyphs.White(0.20),
                d.Red ? Glyphs.SystemRed : Brushes.White, 14, 38, 40);
            var a = d.Action;
            chip.Click += a;
            row.Children.Add(chip);
        }
        return new Border
        {
            Child = row,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = Glyphs.Brush(0x1C, 0x1C, 0x1E, 0.92),
            BorderBrush = Glyphs.White(0.16),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.35 },
        };
    }

    // MARK: - Empty state

    private FrameworkElement EmptyState()
    {
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var tile = new Grid { Width = 96, Height = 96 };
        tile.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.35 },
        }.Bg("Lib.TileBg").Stroke("Lib.TileStroke"));
        var cam = Glyphs.Icon(Glyphs.Camera, 42, Brushes.White);
        Glyphs.SetResourceBrush(cam, "Lib.Text");
        cam.Opacity = 0.9;
        tile.Children.Add(cam);
        s.Children.Add(tile);

        _emptyTitle = Ui.Text("", 26, FontWeights.Bold);
        _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyTitle.Margin = new Thickness(0, 36, 0, 0);
        s.Children.Add(_emptyTitle);
        _emptySub = Ui.Text("", 14, FontWeights.Normal, "Lib.TextSecondary");
        _emptySub.TextAlignment = TextAlignment.Center;
        _emptySub.HorizontalAlignment = HorizontalAlignment.Center;
        _emptySub.Margin = new Thickness(0, 12, 0, 0);
        s.Children.Add(_emptySub);
        UpdateEmptyTexts();

        var coord = AppCoordinator.Shared;
        var pills = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 34, 0, 0) };
        pills.Children.Add(new CapturePill(Glyphs.Region, LibText.T("m_capture_area"), LibText.RegionKeys, true, coord.CaptureRegion));
        pills.Children.Add(new CapturePill("window", LibText.T("lib_pill_window"), LibText.WindowKeys, false, coord.CaptureWindow) { Margin = new Thickness(16, 0, 0, 0) });
        pills.Children.Add(new CapturePill(Glyphs.Monitor, LibText.T("lib_pill_fullscreen"), LibText.FullScreenKeys, false, coord.CaptureFull) { Margin = new Thickness(16, 0, 0, 0) });
        s.Children.Add(pills);
        return s;
    }
}

/// Capsule button of the empty state: icon, title and keycaps; hover brightens and grows 1.5 %,
/// press sinks to 97 % (CapturePillStyle).
internal sealed class CapturePill : Border
{
    private readonly bool _highlighted;
    private readonly ScaleTransform _scale = new(1, 1);
    private bool _hover, _pressed;

    public CapturePill(string glyph, string title, IReadOnlyList<string> keys, bool highlighted, Action action)
    {
        _highlighted = highlighted;
        CornerRadius = new CornerRadius(18);
        Padding = new Thickness(14, 8, 14, 8);
        BorderThickness = new Thickness(highlighted ? 0 : 1);
        RenderTransform = _scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Cursor = Cursors.Hand;
        string fg = highlighted ? "Lib.PillHiText" : "Lib.Text";

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Glyphs.Icon(glyph, 14, Brushes.White);
        Glyphs.SetResourceBrush(icon, fg);
        row.Children.Add(icon);
        var t = Ui.Text(title, 14, FontWeights.SemiBold, fg);
        t.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(t);
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var k in keys)
        {
            var cap = new Border
            {
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), MinWidth = 16,
                Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(caps.Children.Count == 0 ? 0 : 4, 0, 0, 0),
                Child = new TextBlock { Text = k, FontSize = 12, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center },
            }.Bg(highlighted ? "Lib.PillHiKeyBg" : "Lib.KeyBg").Stroke(highlighted ? "Lib.PillHiKeyStroke" : "Lib.KeyStroke");
            ((TextBlock)cap.Child).SetResourceReference(TextBlock.ForegroundProperty, fg);
            caps.Children.Add(cap);
        }
        row.Children.Add(caps);
        Child = row;
        if (!highlighted) this.Stroke("Lib.PillStroke");
        Restyle();

        MouseEnter += (_, _) => { _hover = true; Restyle(); };
        MouseLeave += (_, _) => { _hover = false; _pressed = false; Restyle(); };
        MouseLeftButtonDown += (_, e) => { _pressed = true; CaptureMouse(); Restyle(); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            bool fire = _pressed && Ui.Inside(this, e);
            _pressed = false;
            ReleaseMouseCapture();
            Restyle();
            e.Handled = true;
            if (fire) action();
        };
    }

    private void Restyle()
    {
        this.Bg(_highlighted ? (_hover ? "Lib.PillHiBgHover" : "Lib.PillHiBg") : (_hover ? "Lib.PillBgHover" : "Lib.PillBg"));
        double s = _pressed ? 0.97 : _hover ? 1.015 : 1.0;
        var d = TimeSpan.FromSeconds(_pressed ? 0.10 : 0.12);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s, d) { EasingFunction = CubicBezier.EaseOut });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s, d) { EasingFunction = CubicBezier.EaseOut });
        Opacity = _pressed ? 0.9 : 1;
    }
}

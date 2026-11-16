using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FrameLine.Core;

namespace FrameLine.Editor;

/// The annotation editor (top toolbar capsule + zoomable canvas + bottom zoom capsule), port of
/// MarkupEditorViewController. Hosted by the floating PreviewWindow and embeddable in the library
/// detail pane, exactly like the macOS controller.
/// Disposing it (the library detail panel does when the selection moves) saves pending edits.
public sealed class MarkupEditorView : UserControl, IDisposable
{
    internal static readonly MarkupTool[] ToolOrder =
    {
        MarkupTool.Cursor, MarkupTool.Rectangle, MarkupTool.Arrow, MarkupTool.Pencil,
        MarkupTool.Text, MarkupTool.Pixelate, MarkupTool.Crop,
    };
    private const double MinZoom = 0.1, MaxZoom = 8, FitPadding = 22;


    private readonly MarkupCanvas _canvas;
    private readonly ScrollViewer _scroll = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TextBlock _zoomLabel = new();
    private readonly TextBlock _dimsLabel = new();
    private readonly EditorColorButton _colorButton = new(EditorPalette.DefaultColor);
    private readonly List<EditorToolButton> _toolButtons = new();
    private EditorToolButton _undoButton = null!;
    private EditorToolButton _redoButton = null!;
    private EditorToolButton _moreButton = null!;
    // Static labels, reapplied when the display language changes while the editor is shown.
    private readonly List<(FrameworkElement Element, Func<string> Text)> _tooltips = new();
    private EditorPillButton _copyButton = null!;
    private EditorPillButton _doneButton = null!;
    private EditorToolButton _fitButton = null!;
    private readonly Action _onLanguage;
    private bool _languageHooked;
    private Popup? _colorPopup;
    private long _colorPopupClosedAt;

    private double _zoom = 1;                 // physical screen pixels per image pixel ("100%" = pixel exact)
    private bool _zoomFitMode = true;
    private bool _didInitialFit;
    private bool _isDirty;                    // edits not yet written to the file

    public MarkupEditorView(string imagePath) : this(imagePath, null) { }

    /// `image` = the file already decoded by the caller (avoids decoding it twice).
    internal MarkupEditorView(string imagePath, BitmapSource? image)
    {
        FilePath = imagePath;
        image ??= EditorImageIO.Load(imagePath) ?? BlankImage();
        _canvas = new MarkupCanvas(image) { FocusTarget = this };

        Focusable = true;
        FocusVisualStyle = null;
        Background = EditorPalette.Brush(EditorPalette.Ink);   // charcoal surround, same as the canvas margins
        UseLayoutRounding = true;
        Content = BuildUI();

        _canvas.ImageChanged += () => { _isDirty = true; ImageDidChange(); };
        _canvas.StackChanged += () => { _isDirty = true; UpdateUndoRedo(); };
        UpdateUndoRedo();

        PreviewKeyDown += OnPreviewKeyDown;
        // Hooked only while in the visual tree: the singleton I18n must not keep a detached
        // editor (library selection moved) alive. Loaded also catches a change made while unloaded.
        _onLanguage = () => Dispatcher.BeginInvoke(ApplyStrings);
        Loaded += (_, _) =>
        {
            HookLanguage(true);
            ApplyStrings();
            if (_zoomFitMode) ApplyFit(); else ApplyScale();
        };
        Unloaded += (_, _) => HookLanguage(false);
    }

    private void HookLanguage(bool on)
    {
        if (on == _languageHooked) return;
        _languageHooked = on;
        if (on) I18n.Shared.LanguageChanged += _onLanguage;
        else I18n.Shared.LanguageChanged -= _onLanguage;
    }

    /// Re-reads every label built once in BuildUI (menus, popups and toasts are built on demand).
    private void ApplyStrings()
    {
        foreach (var (element, text) in _tooltips) element.ToolTip = text();
        _copyButton.Title = EditorText.Copy;
        _doneButton.Title = EditorText.Done;
        _fitButton.Label = EditorText.Fit;
    }

    private T Tip<T>(T element, Func<string> text) where T : FrameworkElement
    {
        element.ToolTip = text();
        _tooltips.Add((element, text));
        return element;
    }

    /// Moved to a monitor with another DPI: keep "100%" pixel exact and the fit correct.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_zoomFitMode) ApplyFit(); else ApplyScale();
    }

    /// Path of the file being edited; changes when a non-PNG/JPEG capture is saved as PNG.
    public string FilePath { get; private set; }

    public int ImagePixelWidth => _canvas.PixelWidth;
    public int ImagePixelHeight => _canvas.PixelHeight;

    /// Floating window: Enter triggers Done (not while a text annotation is being typed).
    public bool DefaultDone { get; set; }

    /// Raised after a successful Done (the floating window closes itself on it).
    public event Action? DoneRequested;

    private static BitmapSource BlankImage()
    {
        var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4);
        bmp.Freeze();
        return bmp;
    }

    // MARK: UI construction

    private UIElement BuildUI()
    {
        // Top toolbar: [cursor rect arrow pen Aa pixelate] | [colour] | [crop sparkles] | [undo redo] | [wand ...] | [Copy][Done]
        var tools = new List<UIElement>();
        for (int i = 0; i <= 5; i++) tools.Add(MakeToolButton(i));
        tools.Add(new BarSeparator());

        Tip(_colorButton, () => EditorText.Color);
        _colorButton.Click += (_, _) => ToggleColorPopup();
        tools.Add(_colorButton);
        tools.Add(new BarSeparator());

        tools.Add(MakeToolButton(6));
        tools.Add(MakeIconButton(EditorIcon.Sparkles, () => EditorText.Enhance,
                                 () => EditorToast.Show(EditorText.EnhanceSoon, EditorToast.Kind.Info)));
        tools.Add(new BarSeparator());

        _undoButton = MakeIconButton(EditorIcon.Undo, () => EditorText.Undo, () => _canvas.Undo());
        _redoButton = MakeIconButton(EditorIcon.Redo, () => EditorText.Redo, () => _canvas.Redo());
        tools.Add(_undoButton);
        tools.Add(_redoButton);
        tools.Add(new BarSeparator());

        tools.Add(MakeIconButton(EditorIcon.MagicWand, () => EditorText.MagicEraser,
                                 () => EditorToast.Show(EditorText.EraserSoon, EditorToast.Kind.Info)));
        _moreButton = MakeIconButton(EditorIcon.Ellipsis, () => EditorText.More, ShowMoreMenu);
        tools.Add(_moreButton);
        tools.Add(new BarSeparator());

        _copyButton = new EditorPillButton(EditorText.Copy, EditorIcon.CopyDoc, primary: false);
        _copyButton.Click += (_, _) => CopyImage();
        _doneButton = new EditorPillButton(EditorText.Done, EditorIcon.Checkmark, primary: true);
        _doneButton.Click += (_, _) => Done();
        tools.Add(_copyButton);
        tools.Add(_doneButton);
        var toolCapsule = MakeCapsule(tools, 42, spacing: 3, separatorGap: 7);
        toolCapsule.Margin = new Thickness(0, 14, 0, 0);

        // Bottom zoom bar: "1 648 × 233 px | (-) 100% (+) | Fit 100%"
        _dimsLabel.Text = PixelSizeText;
        StyleLabel(_dimsLabel, FontWeights.Medium, EditorPalette.White(0.78));
        StyleLabel(_zoomLabel, FontWeights.SemiBold, Colors.White);
        _zoomLabel.Width = 40;
        _zoomLabel.TextAlignment = TextAlignment.Center;
        var zoomOut = MakeIconButton(EditorIcon.ZoomOut, () => EditorText.ZoomOut, ZoomOut, iconSize: 13.5, side: 24);
        var zoomIn = MakeIconButton(EditorIcon.ZoomIn, () => EditorText.ZoomIn, ZoomIn, iconSize: 13.5, side: 24);
        var fit = _fitButton = Tip(new EditorToolButton(EditorText.Fit, 11.5, FontWeights.Medium, fixedWidth: null, side: 24), () => EditorText.FitTip);
        fit.Click += (_, _) => ZoomFit();
        var actual = Tip(new EditorToolButton("100%", 11.5, FontWeights.Medium, fixedWidth: null, side: 24), () => EditorText.ActualSizeTip);
        actual.Click += (_, _) => ZoomActual();
        var zoomCapsule = MakeCapsule(new List<UIElement>
        {
            _dimsLabel, new BarSeparator(), zoomOut, _zoomLabel, zoomIn, new BarSeparator(), fit, actual,
        }, 32, spacing: 5, separatorGap: 9);
        zoomCapsule.Margin = new Thickness(0, 0, 0, 12);

        // Canvas: centred when smaller than the viewport (equal charcoal margins all around).
        var host = new Border
        {
            Child = _canvas,
            LayoutTransform = _scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _scroll.Content = host;
        _scroll.Focusable = false;
        _scroll.Background = EditorPalette.Brush(EditorPalette.Ink);
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.Margin = new Thickness(0, 12, 0, 10);
        if (LoadScrollStyles() is { } styles) _scroll.Resources.MergedDictionaries.Add(styles);
        _scroll.SizeChanged += (_, _) => OnViewportResized();
        _scroll.PreviewMouseWheel += OnScrollWheel;
        // Focusing the editor must never scroll the zoomed canvas back to its top-left corner.
        _canvas.RequestBringIntoView += (_, e) => { if (ReferenceEquals(e.OriginalSource, _canvas)) e.Handled = true; };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(toolCapsule, 0);
        Grid.SetRow(_scroll, 1);
        Grid.SetRow(zoomCapsule, 2);
        grid.Children.Add(_scroll);
        grid.Children.Add(toolCapsule);
        grid.Children.Add(zoomCapsule);
        return grid;
    }

    private static ResourceDictionary? LoadScrollStyles()
    {
        try
        {
            return new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/FrameLine;component/Editor/EditorScrollStyles.xaml", UriKind.Absolute),
            };
        }
        catch (Exception ex) when (ex is IOException or System.Windows.Markup.XamlParseException or InvalidOperationException)
        {
            return null;   // stock scroll bars are an acceptable fallback
        }
    }

    private static void StyleLabel(TextBlock t, FontWeight weight, Color color)
    {
        t.FontSize = 11.5;
        t.FontWeight = weight;
        t.Foreground = EditorPalette.Brush(color);
        t.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);   // monospaced digits
    }

    private EditorToolButton MakeToolButton(int index)
    {
        var tool = ToolOrder[index];
        var b = tool == MarkupTool.Text
            ? new EditorToolButton("Aa", 12.5, FontWeights.SemiBold)
            : new EditorToolButton(IconFor(tool));
        Tip(b, () => LabelFor(tool));
        b.Tag = index;
        b.IsSelectedTool = index == 0;
        b.Click += (_, _) => SelectTool(b);
        _toolButtons.Add(b);
        return b;
    }

    private EditorToolButton MakeIconButton(EditorIcon icon, Func<string> tip, Action action, double iconSize = 15, double side = 26)
    {
        var b = Tip(new EditorToolButton(icon, iconSize, side), tip);
        b.Click += (_, _) => action();
        return b;
    }

    private static EditorIcon IconFor(MarkupTool t) => t switch
    {
        MarkupTool.Cursor => EditorIcon.Cursor,
        MarkupTool.Rectangle => EditorIcon.Rectangle,
        MarkupTool.Arrow => EditorIcon.Arrow,
        MarkupTool.Pencil => EditorIcon.Pencil,
        MarkupTool.Pixelate => EditorIcon.Pixelate,
        _ => EditorIcon.Crop,
    };

    private static string LabelFor(MarkupTool t) => t switch
    {
        MarkupTool.Cursor => EditorText.ToolSelect,
        MarkupTool.Rectangle => EditorText.ToolRectangle,
        MarkupTool.Arrow => EditorText.ToolArrow,
        MarkupTool.Pencil => EditorText.ToolPencil,
        MarkupTool.Text => EditorText.ToolText,
        MarkupTool.Pixelate => EditorText.ToolPixelate,
        _ => EditorText.ToolCrop,
    };

    /// Floating dark capsule: rounded background with its own shadow layer (keeps text crisp),
    /// items spaced by `spacing`, with more air around separators (reads as groups).
    private static FrameworkElement MakeCapsule(IReadOnlyList<UIElement> items, double height, double spacing, double separatorGap)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is FrameworkElement fe)
            {
                bool nearSeparator = items[i] is BarSeparator || (i > 0 && items[i - 1] is BarSeparator);
                fe.Margin = new Thickness(i == 0 ? 0 : nearSeparator ? separatorGap : spacing, 0, 0, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            stack.Children.Add(items[i]);
        }
        var background = new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            Background = EditorPalette.Brush(EditorPalette.Capsule),
            BorderBrush = EditorPalette.Brush(EditorPalette.White(0.10)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        var capsule = new Grid { Height = height, HorizontalAlignment = HorizontalAlignment.Center };
        capsule.Children.Add(background);
        capsule.Children.Add(stack);
        return capsule;
    }

    /// Pixel dimensions with a narrow no-break space as thousands separator ("1 648 × 233 px").
    private string PixelSizeText => $"{Grouped(_canvas.PixelWidth)} × {Grouped(_canvas.PixelHeight)} px";

    private static string Grouped(int n)
    {
        var nf = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        nf.NumberGroupSeparator = " ";
        nf.NumberGroupSizes = new[] { 3 };
        return n.ToString("#,0", nf);
    }

    // MARK: tools and colour

    private void SelectTool(EditorToolButton sender)
    {
        _canvas.EndTextEditing();
        int idx = sender.Tag is int i ? i : 0;
        _canvas.Tool = idx >= 0 && idx < ToolOrder.Length ? ToolOrder[idx] : MarkupTool.Cursor;
        foreach (var b in _toolButtons) b.IsSelectedTool = ReferenceEquals(b, sender);
    }

    private void SetColor(Color c)
    {
        _canvas.StrokeColor = c;
        _colorButton.Color = c;
    }

    private void ToggleColorPopup()
    {
        if (_colorPopup is { IsOpen: true }) { _colorPopup.IsOpen = false; _colorPopup = null; return; }
        // The click that light-dismissed the palette must not reopen it straight away.
        if (Environment.TickCount64 - _colorPopupClosedAt < 250) return;

        var grid = new StackPanel { Orientation = Orientation.Vertical };
        StackPanel? row = null;
        for (int i = 0; i < EditorPalette.Presets.Length; i++)
        {
            if (i % 3 == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0) };
                grid.Children.Add(row);
            }
            var c = EditorPalette.Presets[i];
            var sw = new RoundSwatchButton(c, EditorPalette.SameColor(c, _canvas.StrokeColor))
            {
                Margin = new Thickness(i % 3 == 0 ? 0 : 8, 0, 0, 0),
            };
            sw.Click += (_, _) => { SetColor(sw.SwatchColor); CloseColorPopup(); _colorPopupClosedAt = 0; };
            row!.Children.Add(sw);
        }
        var custom = new EditorToolButton(EditorText.CustomColor, 12, FontWeights.Medium, fixedWidth: null, side: 24)
        {
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        custom.Click += (_, _) => { CloseColorPopup(); OpenCustomColor(); };
        grid.Children.Add(custom);

        var popup = new Popup
        {
            PlacementTarget = _colorButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = EditorPalette.Brush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderBrush = EditorPalette.Brush(EditorPalette.White(0.10)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 8, 8),
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.35 },
                Child = grid,
            },
        };
        popup.Closed += (_, _) =>
        {
            _colorPopupClosedAt = Environment.TickCount64;
            if (ReferenceEquals(_colorPopup, popup)) _colorPopup = null;
        };
        _colorPopup = popup;
        popup.IsOpen = true;
    }

    private void CloseColorPopup()
    {
        if (_colorPopup != null) _colorPopup.IsOpen = false;
        _colorPopup = null;
    }

    /// The system colour dialog (modal on Windows, where macOS has a floating colour panel).
    private void OpenCustomColor()
    {
        var current = _canvas.StrokeColor;
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AnyColor = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        var owner = Window.GetWindow(this);
        var hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
        var result = hwnd != IntPtr.Zero ? dialog.ShowDialog(new Win32Owner(hwnd)) : dialog.ShowDialog();
        if (result == System.Windows.Forms.DialogResult.OK)
            SetColor(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
    }

    private sealed class Win32Owner : System.Windows.Forms.IWin32Window
    {
        public Win32Owner(IntPtr handle) { Handle = handle; }
        public IntPtr Handle { get; }
    }

    // MARK: "..." menu

    private void ShowMoreMenu()
    {
        var iconColor = Color.FromRgb(0x30, 0x30, 0x30);
        MenuItem Item(string title, EditorIcon icon, Action action)
        {
            var mi = new MenuItem { Header = title, Icon = icon.ToShape(iconColor) };
            mi.Click += (_, _) => action();
            return mi;
        }
        var menu = new ContextMenu
        {
            PlacementTarget = _moreButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
        };
        menu.Items.Add(Item(EditorText.OcrCopy, EditorIcon.TextViewfinder, OcrCopy));
        menu.Items.Add(Item(EditorText.SaveCopy, EditorIcon.SaveDown, SaveCopy));
        menu.Items.Add(Item(EditorText.Pin, EditorIcon.Pin, PinToScreen));
        menu.Items.Add(new Separator());
        var reset = Item(EditorText.ClearAnnotations, EditorIcon.Trash, () => _canvas.ClearAnnotations());
        reset.IsEnabled = _canvas.HasMarkups;
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    private async void OcrCopy()
    {
        var path = FilePath;
        bool ok;
        try { ok = await TextRecognizer.CopyTextAsync(path); }
        catch (Exception) { ok = false; }
        EditorToast.Show(ok ? EditorText.TextCopied : EditorText.NoText, ok ? EditorToast.Kind.Success : EditorToast.Kind.Info);
    }

    private void SaveCopy()
    {
        _canvas.EndTextEditing();
        var flat = _canvas.Flatten();
        if (flat == null) return;
        byte[] data;
        try { data = EditorImageIO.EncodePng(flat); }
        catch (Exception) { EditorToast.Show(EditorText.SaveFailed, EditorToast.Kind.Warning); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = $"{EditorText.PngFilter} (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = EditorText.CopyFileName(Path.GetFileNameWithoutExtension(FilePath)) + ".png",
            InitialDirectory = Path.GetDirectoryName(FilePath) ?? "",
            OverwritePrompt = true,
        };
        // Owned by the editor window: the floating (topmost) editor would otherwise hide it.
        var owner = Window.GetWindow(this);
        bool? ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, data);
            EditorToast.Show(EditorText.Saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Debug.WriteLine("FrameLine — copy: " + ex.Message);
            EditorToast.Show(EditorText.SaveFailed, EditorToast.Kind.Warning);
        }
    }

    private void PinToScreen()
    {
        _canvas.EndTextEditing();
        SaveImage();                                          // the pin reflects the current annotations
        FrameLine.Library.PinnedShotWindow.Show(FilePath);
    }

    // MARK: undo / copy / done / save

    private void UpdateUndoRedo()
    {
        _undoButton.IsEnabled = _canvas.CanUndo;
        _redoButton.IsEnabled = _canvas.CanRedo;
    }

    private void CopyImage()
    {
        _canvas.EndTextEditing();
        var flat = _canvas.Flatten();
        if (flat == null) return;
        if (EditorImageIO.CopyImage(flat)) EditorToast.Show(EditorText.Copied);
    }

    private void Done()
    {
        _canvas.EndTextEditing();
        // Write failure: do NOT close, closing would drop the annotations while claiming "Saved".
        if (!SaveImage())
        {
            EditorToast.Show(EditorText.SaveFailed, EditorToast.Kind.Warning);
            return;
        }
        EditorToast.Show(EditorText.Saved);
        DoneRequested?.Invoke();
    }

    /// Writes the annotated state to the file. JPEG files are rewritten as JPEG in place (same
    /// quality as captures); PNG in place; any other format becomes a .png next to it and the
    /// original is removed. False = nothing could be written.
    private bool SaveImage()
    {
        var flat = _canvas.Flatten();
        if (flat == null) return false;
        var original = FilePath;
        var ext = Path.GetExtension(original).ToLowerInvariant();
        bool isJpg = ext is ".jpg" or ".jpeg";
        var target = ext == ".png" || isJpg ? original : Path.ChangeExtension(original, ".png");
        try
        {
            var data = isJpg ? EditorImageIO.EncodeJpeg(flat) : EditorImageIO.EncodePng(flat);
            EditorImageIO.WriteAtomic(target, data);
            if (!string.Equals(target, original, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(original); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            FilePath = target;
            _isDirty = false;
            ReloadLibrary(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("FrameLine — annotated save: " + ex.Message);
            return false;
        }
    }

    /// ReloadFile, not Reload: the listing may look identical after an in-place save, and the
    /// thumbnails of this capture must still be rebuilt.
    private static void ReloadLibrary(string path)
    {
        try { CaptureLibrary.Shared.ReloadFile(path); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("FrameLine — library reload: " + ex.Message); }
    }

    /// Embedded editor being torn down (another capture selected, a new capture arrived): writes
    /// pending annotations instead of silently dropping them.
    public void SaveIfEdited()
    {
        _canvas.EndTextEditing();
        if (!_isDirty) return;
        // Capture trashed / deleted / moved meanwhile: never recreate it.
        if (!File.Exists(FilePath)) { _isDirty = false; return; }
        DateTime? oldDate = null;
        try { oldDate = File.GetLastWriteTimeUtc(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (!SaveImage())
        {
            EditorToast.Show(EditorText.SaveFailed, EditorToast.Kind.Warning);
            return;
        }
        // Implicit save: the capture keeps its place in the library (it would otherwise jump ahead
        // of the capture just taken). +1 ms so views still re-read the dimensions.
        if (oldDate is { } d)
        {
            try { File.SetLastWriteTimeUtc(FilePath, d.AddMilliseconds(1)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            ReloadLibrary(FilePath);
        }
    }

    public void Dispose() => SaveIfEdited();

    // MARK: keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_canvas.IsEditingText) return;                    // the text field owns Enter, Ctrl+Z, Ctrl+C...
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (mods == ModifierKeys.Control && key == Key.Z) { _canvas.Undo(); e.Handled = true; }
        else if ((mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z) ||
                 (mods == ModifierKeys.Control && key == Key.Y)) { _canvas.Redo(); e.Handled = true; }
        else if (mods == ModifierKeys.None && key is Key.Enter && DefaultDone) { Done(); e.Handled = true; }
    }

    // MARK: zoom

    private void ZoomOut() { _zoomFitMode = false; SetZoom(_zoom * 0.8, null); }
    private void ZoomIn() { _zoomFitMode = false; SetZoom(_zoom * 1.25, null); }
    private void ZoomFit() { _zoomFitMode = true; ApplyFit(); }
    private void ZoomActual() { _zoomFitMode = false; SetZoom(1.0, null); }

    /// Ctrl+wheel (and precision touchpad pinch, which Windows delivers as Ctrl+wheel) zooms around
    /// the pointer, like the macOS pinch; Shift+wheel scrolls horizontally.
    private void OnScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _zoomFitMode = false;
            SetZoom(_zoom * Math.Pow(1.1, e.Delta / 120.0), e.GetPosition(_scroll));
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void OnViewportResized()
    {
        if (_scroll.ActualWidth <= 0 || _scroll.ActualHeight <= 0) return;
        if (!_didInitialFit) { _didInitialFit = true; ApplyFit(); }
        else if (_zoomFitMode) ApplyFit();
    }

    /// Fits the image in the viewport with a charcoal margin all around.
    private void ApplyFit()
    {
        double availW = _scroll.ActualWidth - 2 * FitPadding, availH = _scroll.ActualHeight - 2 * FitPadding;
        if (availW <= 0 || availH <= 0 || _canvas.PixelWidth <= 0 || _canvas.PixelHeight <= 0) return;
        double dipPerPixel = Math.Min(availW / _canvas.PixelWidth, availH / _canvas.PixelHeight);
        // No scroll anchoring here: the fitted image is centred by layout (and this runs from SizeChanged).
        _zoom = Math.Clamp(dipPerPixel * Dpi, MinZoom, MaxZoom);
        ApplyScale();
    }

    /// `anchor` (viewport coordinates) stays over the same image pixel; null = viewport centre.
    private void SetZoom(double zoom, Point? anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var a = anchor ?? new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);
        Point imagePoint = default;
        bool canAnchor = _canvas.IsVisible && PresentationSource.FromVisual(_canvas) != null;
        if (canAnchor) imagePoint = _scroll.TranslatePoint(a, _canvas);
        _zoom = zoom;
        ApplyScale();
        if (canAnchor)
        {
            _scroll.UpdateLayout();
            var now = _canvas.TranslatePoint(imagePoint, _scroll);
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + (now.X - a.X));
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + (now.Y - a.Y));
        }
    }

    private void ApplyScale()
    {
        double s = _zoom / Dpi;
        _scale.ScaleX = s;
        _scale.ScaleY = s;
        _canvas.Zoom = _zoom;
        _canvas.InvalidateVisual();
        _zoomLabel.Text = $"{(int)Math.Round(_zoom * 100)}%";
    }

    /// Physical pixels per DIP of the monitor the editor is on (PerMonitorV2).
    private double Dpi => Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);

    private void ImageDidChange()
    {
        if (_zoomFitMode)
        {
            _canvas.UpdateLayout();
            ApplyFit();
        }
        else ApplyScale();
        UpdateUndoRedo();
        _dimsLabel.Text = PixelSizeText;                       // dimensions change after a crop
    }
}

/// Vertical bar separator, recognisable for the group spacing.
internal sealed class BarSeparator : FrameworkElement
{
    private static readonly Brush Fill = EditorPalette.Brush(EditorPalette.White(0.16));

    public BarSeparator()
    {
        Width = 1;
        Height = 19;
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Fill, null, new Rect(0, 0, 1, Height));
}

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FrameLine.Shell;

namespace FrameLine.Library;

/// One sidebar card (CaptureRow of SidebarView.swift), built once and RECYCLED by the virtualizing
/// list: Bind/Unbind swap the entry. The capture fills the card edge to edge from a thumbnail
/// pre-cropped to the card ratio (2.6), never letterboxed.
internal sealed class CaptureRowView : Border
{
    private readonly Image _image;
    private readonly FrameworkElement _placeholder;
    private readonly FrameworkElement _videoBadge;
    private readonly StackPanel _hoverActions;
    private readonly MiddleEllipsisText _name;
    private readonly TextBlock _subtitle;
    private CaptureEntry? _entry;
    private bool _hovering;

    public CaptureRowView()
    {
        Padding = new Thickness(6);
        Margin = new Thickness(12, 4, 22, 4);   // right = the floating scroll thumb's lane
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(2);
        BorderBrush = Brushes.Transparent;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;

        var thumb = new Grid().Bg("Lib.SidebarBg");
        _image = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        thumb.Children.Add(_image);
        _placeholder = Glyphs.Icon(Glyphs.Photo, 26, Brushes.Gray);
        Glyphs.SetResourceBrush(_placeholder, "Lib.TextSecondary");
        thumb.Children.Add(_placeholder);
        _videoBadge = VideoBadge.Create(46);
        thumb.Children.Add(_videoBadge);

        var box = new AspectBox { Aspect = CaptureEntry.CardAspect, Radius = 9, Child = thumb };
        var frame = new Grid();
        frame.Children.Add(box);
        frame.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), IsHitTestVisible = false,
        }.Stroke("Lib.ThumbStroke"));

        // Restore / Copy / Pin, top-right of the thumbnail on hover (images only).
        _hoverActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 3, 5, 3) };
        _hoverActions.Children.Add(Inline(Glyphs.FullScreen, LibText.T("lib_restore_to_screen"), () => Defer(() => AppCoordinator.Shared.PinToScreen(Path))));
        _hoverActions.Children.Add(Inline(Glyphs.Copy, LibText.T("lib_copy"), () => AppCoordinator.Shared.CopyImage(Path)));
        _hoverActions.Children.Add(Inline(Glyphs.Pin, LibText.T("lib_pin_to_screen"), () => Defer(() => AppCoordinator.Shared.PinToScreen(Path))));
        var capsule = new Border
        {
            Background = Glyphs.Black(0.55),
            BorderBrush = Glyphs.White(0.10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Child = _hoverActions,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        capsule.MouseLeftButtonUp += (_, e) => e.Handled = true;
        frame.Children.Add(capsule);
        _capsule = capsule;

        _name = new MiddleEllipsisText { FontSize = 11, FontWeight = FontWeights.SemiBold };
        _name.SetResourceReference(MiddleEllipsisText.ForegroundProperty, "Lib.Text");
        _subtitle = new TextBlock { FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Lib.TextSecondary");
        var texts = new StackPanel { Margin = new Thickness(3, 6, 3, 0) };
        texts.Children.Add(_name);
        texts.Children.Add(_subtitle);

        var stack = new StackPanel();
        stack.Children.Add(frame);
        stack.Children.Add(texts);
        Child = stack;

        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        MouseLeftButtonUp += OnClick;
        ContextMenu = new ContextMenu();
        ContextMenu.SetResourceReference(StyleProperty, "FL.ContextMenu");
        ContextMenuOpening += (_, e) => { if (_entry == null) e.Handled = true; else FillMenu(ContextMenu); };
    }

    private readonly Border _capsule;
    private string Path => _entry?.Path ?? "";

    public CaptureEntry? Entry => _entry;

    public void Bind(CaptureEntry entry)
    {
        if (ReferenceEquals(_entry, entry)) return;
        Unbind();
        _entry = entry;
        entry.AddView();
        entry.PropertyChanged += OnEntryChanged;
        _name.Text = entry.Name;
        Refresh();
        entry.RequestCover(Dispatcher);
    }

    public void Unbind()
    {
        if (_entry == null) return;
        _entry.PropertyChanged -= OnEntryChanged;
        _entry.RemoveView();
        _entry = null;
        _image.Source = null;
        SetHover(false);
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_entry == null) return;
        switch (e.PropertyName)
        {
            case nameof(CaptureEntry.Cover):
                Refresh();
                if (_entry.Cover == null) _entry.RequestCover(Dispatcher);   // rewritten in place: new thumbnail
                break;
            case nameof(CaptureEntry.Subtitle):
            case nameof(CaptureEntry.IsSelected):
            case nameof(CaptureEntry.Created):
                Refresh();
                break;
        }
    }

    private void Refresh()
    {
        var e = _entry;
        if (e == null) return;
        _image.Source = e.Cover;
        _placeholder.Visibility = e.Cover == null && !e.IsVideo ? Visibility.Visible : Visibility.Collapsed;
        _videoBadge.Visibility = e.IsVideo ? Visibility.Visible : Visibility.Collapsed;
        _subtitle.Text = e.Subtitle;
        if (e.IsSelected) SetResourceReference(BorderBrushProperty, "Lib.SelectedStroke");
        else BorderBrush = Brushes.Transparent;
        UpdateBackground();
    }

    private void UpdateBackground()
    {
        if (_hovering && _entry is { IsSelected: false }) SetResourceReference(BackgroundProperty, "Lib.Hover");
        else Background = Brushes.Transparent;
    }

    private void SetHover(bool on)
    {
        _hovering = on;
        UpdateBackground();
        bool show = on && _entry is { IsVideo: false };   // restore / copy / pin make no sense on a video
        _capsule.IsHitTestVisible = show;
        _capsule.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromSeconds(0.12))
        {
            EasingFunction = CubicBezier.EaseOut,
        });
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_entry == null) return;
        // Finder-style: click = only this one, Ctrl+click = toggle, Shift+click = range.
        LibraryModel.Shared.HandleClick(_entry.Path, Ui.IsCtrl, Ui.IsShift);
        ItemsControl.ItemsControlFromItemContainer(this)?.Focus();
        e.Handled = true;
    }

    /// Presenting a window from inside the click handler can be swallowed (the macOS app hit
    /// this with SwiftUI buttons); deferring one dispatcher turn keeps both platforms identical.
    private void Defer(Action a) => Dispatcher.BeginInvoke(a);

    private static FrameworkElement Inline(string glyph, string tip, Action action)
    {
        var icon = Glyphs.Icon(glyph, 11, Glyphs.White(0.82));
        var b = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
            ToolTip = tip, Child = icon, Margin = new Thickness(1, 0, 1, 0),
        };
        b.MouseEnter += (_, _) => { b.Background = Glyphs.White(0.18); Glyphs.SetBrush(icon, Brushes.White); };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; Glyphs.SetBrush(icon, Glyphs.White(0.82)); };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (b.IsMouseOver) action(); };
        return b;
    }

    // MARK: - Context menu (every action goes through AppCoordinator)

    private void FillMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var e = _entry!;
        var path = e.Path;
        var coord = AppCoordinator.Shared;
        var model = LibraryModel.Shared;
        if (!e.IsVideo)
        {
            menu.Items.Add(Item(LibText.T("lib_annotate"), Glyphs.Edit, () => coord.Annotate(path)));
            menu.Items.Add(Item(LibText.T("lib_copy_image"), Glyphs.Copy, () => coord.CopyImage(path)));
            menu.Items.Add(Item(LibText.T("lib_copy_text"), Glyphs.Text, () => coord.CopyText(path)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(LibText.T("lib_restore_to_screen"), Glyphs.FullScreen, () => coord.PinToScreen(path)));
            menu.Items.Add(Item(LibText.T("lib_pin_to_screen"), Glyphs.Pin, () => coord.PinToScreen(path)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item(LibText.T("lib_show_in_explorer"), Glyphs.Folder, () => coord.RevealInExplorer(path)));
        menu.Items.Add(Item(LibText.T("lib_export"), Glyphs.Share, () => coord.ExportCapture(path)));
        menu.Items.Add(new Separator());
        // Right-click on a card that is part of an active multi-selection: the trash targets the
        // whole selection, like Finder.
        int n = model.SelectedPaths.Count;
        bool multi = n > 1 && e.IsSelected;
        menu.Items.Add(Item(multi ? LibText.F("lib_move_to_trash_n", n) : LibText.T("lib_move_to_trash"), Glyphs.Delete,
            () => { if (multi) model.TrashSelection(); else model.TrashOne(path); }));
    }

    private static MenuItem Item(string header, string glyph, Action action)
    {
        var mi = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = Glyphs.IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
        };
        mi.Click += (_, _) => action();
        return mi;
    }
}

/// The virtualized gallery: an ItemsControl whose containers are CaptureRowView (recycled), in a
/// pixel-scrolling VirtualizingStackPanel (2,000+ captures), with the native bar hidden behind the
/// floating OverlayScrollThumb.
internal sealed class SidebarList : ItemsControl
{
    public ScrollViewer? Scroller { get; private set; }

    public SidebarList()
    {
        Focusable = true;
        FocusVisualStyle = null;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Page);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(0.5));
        ScrollViewer.SetCanContentScroll(this, true);

        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_Scroll");
        sv.SetValue(ScrollViewer.CanContentScrollProperty, true);
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(FocusableProperty, false);
        sv.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = sv };
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Scroller = GetTemplateChild("PART_Scroll") as ScrollViewer;
    }

    protected override DependencyObject GetContainerForItemOverride() => new CaptureRowView();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is CaptureRowView;
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is CaptureRowView row && item is CaptureEntry e) row.Bind(e);
    }
    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is CaptureRowView row) row.Unbind();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var model = LibraryModel.Shared;
        if (e.Key is Key.Up or Key.Down)
        {
            model.MoveFocus(e.Key == Key.Up ? -1 : 1, Ui.IsShift);
            BringFocusIntoView();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            model.TrashSelection();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    public void BringFocusIntoView()
    {
        var focus = LibraryModel.Shared.Focus;
        if (focus == null) return;
        int i = LibraryModel.Shared.IndexOf(focus);
        if (i < 0) return;
        if (FindPanel(this) is VirtualizingStackPanel panel) panel.BringIndexIntoViewPublic(i);
    }

    private static VirtualizingStackPanel? FindPanel(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is VirtualizingStackPanel p) return p;
            if (FindPanel(c) is { } found) return found;
        }
        return null;
    }
}

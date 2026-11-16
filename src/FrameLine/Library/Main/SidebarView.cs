using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FrameLine.Shell;

namespace FrameLine.Library;

/// Left column (SidebarView.swift): "Captures" + count + "Delete N" pill, the virtualized
/// gallery, and the bottom tool bar (import, folder, refresh, trash, settings).
internal sealed class SidebarView : DockPanel
{
    private readonly TextBlock _count;
    private readonly Border _deletePill;
    private readonly TextBlock _deleteLabel;
    private readonly SidebarList _list;
    private readonly Grid _listHost;
    private readonly FrameworkElement _empty;
    private readonly FlatIconButton _trash;

    public SidebarList List => _list;

    public SidebarView()
    {
        LastChildFill = true;

        // Header
        var header = new DockPanel { Margin = new Thickness(16, 8, 16, 10), LastChildFill = false };
        var title = Ui.Text(LibText.T("lib_captures"), 15, FontWeights.Bold);
        DockPanel.SetDock(title, Dock.Left);
        header.Children.Add(title);
        _count = Ui.Text("0", 11, FontWeights.SemiBold);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 0, 0, 0),
            Child = _count, VerticalAlignment = VerticalAlignment.Center,
        }.Bg("Lib.Badge");
        DockPanel.SetDock(badge, Dock.Left);
        header.Children.Add(badge);

        _deleteLabel = Ui.Text("", 11.5, FontWeights.SemiBold);
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal };
        var trashIcon = Glyphs.Icon(Glyphs.Delete, 10, Brushes.White, bold: true);
        Glyphs.SetResourceBrush(trashIcon, "Lib.Text");
        pillContent.Children.Add(trashIcon);
        _deleteLabel.Margin = new Thickness(5, 0, 0, 0);
        pillContent.Children.Add(_deleteLabel);
        _deletePill = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 5, 11, 5), BorderThickness = new Thickness(0.5),
            Child = pillContent, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            Cursor = System.Windows.Input.Cursors.Hand,
        }.Bg("Lib.DeleteBg").Stroke("Lib.DeleteStroke");
        _deletePill.MouseEnter += (_, _) => _deletePill.Bg("Lib.DeleteBgHover").Stroke("Lib.DeleteStrokeHover");
        _deletePill.MouseLeave += (_, _) => _deletePill.Bg("Lib.DeleteBg").Stroke("Lib.DeleteStroke");
        _deletePill.MouseLeftButtonUp += (_, e) => { e.Handled = true; LibraryModel.Shared.TrashSelection(); };
        DockPanel.SetDock(_deletePill, Dock.Right);
        header.Children.Add(_deletePill);
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        // Bottom tool bar
        var bar = new DockPanel { Margin = new Thickness(16, 10, 16, 10), LastChildFill = false };
        var import = new FlatIconButton(Glyphs.Photo, LibText.T("lib_import_image"));
        import.Click += () => AppCoordinator.Shared.ImportImages();
        var folder = new FlatIconButton(Glyphs.Folder, LibText.T("lib_open_folder"));
        folder.Click += () => AppCoordinator.Shared.OpenCapturesFolder();
        var refresh = new FlatIconButton(Glyphs.Refresh, LibText.T("lib_refresh"));
        refresh.Click += () => LibraryModel.Shared.RequestReload();
        _trash = new FlatIconButton(Glyphs.Delete, LibText.T("lib_trash_none"), destructive: true);
        _trash.Click += () => LibraryModel.Shared.TrashSelection();
        var settings = new FlatIconButton(Glyphs.Settings, LibText.T("lib_settings"));
        // Deferred one turn, like the macOS button (presenting a window mid-click can be swallowed).
        settings.Click += () => Dispatcher.BeginInvoke(() => AppCoordinator.Shared.OpenPreferences());
        foreach (var b in new[] { import, folder, refresh, _trash })
        {
            b.Margin = new Thickness(0, 0, 16, 0);
            DockPanel.SetDock(b, Dock.Left);
            bar.Children.Add(b);
        }
        DockPanel.SetDock(settings, Dock.Right);
        bar.Children.Add(settings);
        DockPanel.SetDock(bar, Dock.Bottom);
        Children.Add(bar);
        var divider = new Border { Height = 1 }.Bg("Lib.Divider");
        DockPanel.SetDock(divider, Dock.Bottom);
        Children.Add(divider);

        // Gallery
        _list = new SidebarList { ItemsSource = LibraryModel.Shared.Items };
        _listHost = new Grid();
        _listHost.Children.Add(_list);
        _list.Loaded += (_, _) =>
        {
            if (_list.Scroller != null && _listHost.Children.Count == 1)
                _listHost.Children.Add(new OverlayScrollThumb(_list.Scroller));
        };
        _empty = EmptyState();
        var middle = new Grid();
        middle.Children.Add(_listHost);
        middle.Children.Add(_empty);
        Children.Add(middle);

        LibraryModel.Shared.ItemsChanged += Update;
        LibraryModel.Shared.SelectionChanged += Update;
        Update();
    }

    public void Detach()
    {
        LibraryModel.Shared.ItemsChanged -= Update;
        LibraryModel.Shared.SelectionChanged -= Update;
        _list.ItemsSource = null;   // unbinds every recycled row (view refs, handlers)
    }

    private void Update()
    {
        var m = LibraryModel.Shared;
        int count = m.Items.Count;
        _count.Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _listHost.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _trash.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

        int n = m.SelectedPaths.Count;
        bool show = n > 0;
        if (show && _deletePill.Visibility != Visibility.Visible)
        {
            _deletePill.Visibility = Visibility.Visible;
            _deletePill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.14)));
        }
        else if (!show) _deletePill.Visibility = Visibility.Collapsed;
        _deleteLabel.Text = n > 1 ? LibText.F("lib_delete_n", n) : LibText.T("lib_delete");
        _deletePill.ToolTip = n > 1 ? LibText.F("lib_delete_tip_many", n) : LibText.T("lib_delete_tip_one");

        _trash.Enabled = m.ActionableSelection.Count > 0;
        _trash.Tip = n == 0 ? LibText.T("lib_trash_none") : n > 1 ? LibText.F("lib_trash_many", n) : LibText.T("lib_trash_one");
    }

    private static FrameworkElement EmptyState()
    {
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        var icon = Glyphs.Icon(Glyphs.Photo, 32, Brushes.Gray);
        Glyphs.SetResourceBrush(icon, "Lib.TextSecondary");
        s.Children.Add(icon);
        var t = Ui.Text(LibText.T("lib_no_captures"), 13, FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 8, 0, 0);
        s.Children.Add(t);
        var hint = Ui.Text(LibText.F("lib_sidebar_empty_hint", LibText.RegionLabel), 11, FontWeights.Normal, "Lib.TextSecondary");
        hint.TextAlignment = TextAlignment.Center;
        hint.TextWrapping = TextWrapping.Wrap;
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(22, 8, 22, 0);
        s.Children.Add(hint);
        return s;
    }
}

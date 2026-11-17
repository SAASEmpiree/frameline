using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FrameLine.Library;

/// Drag-and-drop import on the sidebar and the detail panel (ContentView.swift's DropImport):
/// files dropped there are copied into the capture folder; an accent frame shows the target.
internal static class DropImport
{
    public static Grid Wrap(FrameworkElement content)
    {
        var host = new Grid { AllowDrop = true, Background = System.Windows.Media.Brushes.Transparent };
        host.Children.Add(content);
        var frame = new Border
        {
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(4),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        }.Stroke("Lib.Accent");
        host.Children.Add(frame);

        void Over(object s, DragEventArgs e)
        {
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            frame.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }
        host.DragEnter += Over;
        host.DragOver += Over;
        host.DragLeave += (_, e) =>
        {
            // DragLeave also fires when the pointer moves onto a child: only hide when it really left.
            var p = e.GetPosition(host);
            if (p.X <= 0 || p.Y <= 0 || p.X >= host.ActualWidth || p.Y >= host.ActualHeight)
                frame.Visibility = Visibility.Collapsed;
        };
        host.Drop += (_, e) =>
        {
            frame.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                LibraryModel.Shared.ImportFiles(files.ToList());
            e.Handled = true;
        };
        return host;
    }
}

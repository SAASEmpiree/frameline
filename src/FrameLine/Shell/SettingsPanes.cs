using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FrameLine.Capture;
using FrameLine.Core;
using FrameLine.Core.Mcp;
using FrameLine.Platform;
using Microsoft.Win32;

namespace FrameLine.Shell;

/// The four Settings panes (SettingsView.swift), built in code so a language change simply
/// rebuilds them. Every control writes Preferences directly, like the macOS @AppStorage bindings.
internal sealed class SettingsPanes
{
    private static readonly Brush Green = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));

    private readonly Window _owner;
    private TextBlock? _mcpStatusText;
    private Ellipse? _mcpDot;
    private Button? _mcpCopy, _mcpRegenerate;
    private Action? _rebuildCurrent;

    /// True while a control of this window writes Preferences (its own Changed echo is ignored).
    public bool IsWriting { get; private set; }

    public SettingsPanes(Window owner) => _owner = owner;

    /// A preference changed elsewhere (tray, another window, MCP): re-read the visible pane.
    public void Refresh(string property) => _rebuildCurrent?.Invoke();

    private void Write(Action write)
    {
        IsWriting = true;
        try { write(); }
        finally { IsWriting = false; }
    }

    // MARK: - General (Appearance: display language + theme · Startup · MCP)

    public UIElement GeneralPane()
    {
        _rebuildCurrent = null;
        var p = Preferences.Shared;
        var languages = new List<(string, string)> { ("system", Tr.L("system")) };
        foreach (var code in I18n.SupportedLanguages) languages.Add((code, I18n.NativeName(code)));

        var root = new StackPanel();
        root.Children.Add(Section(Tr.L("sec_appearance"),
            Picker(Tr.L("display_language"), languages, p.DisplayLanguage, code => I18n.Shared.Set(code)),
            Picker(Tr.L("theme"), new[] { ("system", Tr.L("theme_system")), ("light", Tr.L("theme_light")), ("dark", Tr.L("theme_dark")) },
                p.Theme, v => Write(() => { p.Theme = v; ThemeManager.Apply(); }))));

        CheckBox? login = null;
        login = Toggle(Tr.L("launch_at_login"), LaunchAtLogin.IsEnabled, on =>
        {
            LaunchAtLogin.IsEnabled = on;
            bool real = LaunchAtLogin.IsEnabled;             // reflect the real state
            if (login != null && login.IsChecked != real) login.IsChecked = real;
        });
        root.Children.Add(Section(Tr.L("sec_startup"), login));
        root.Children.Add(McpSection());
        _rebuildCurrent = () => Swap(GeneralPane);
        return root;
    }

    private UIElement McpSection()
    {
        var toggle = Toggle(Tr.L("mcp_enable"), Preferences.Shared.McpServerEnabled,
            on => Write(() => McpController.Shared.SetEnabled(on)));

        _mcpDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        _mcpStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center };
        _mcpStatusText.SetResourceReference(TextBlock.ForegroundProperty, "FL.TextSecondary");
        var statusValue = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        statusValue.Children.Add(_mcpDot);
        statusValue.Children.Add(_mcpStatusText);
        var status = LabeledRow(Tr.L("mcp_status"), statusValue);

        _mcpCopy = ButtonFor(Tr.L("mcp_copy_config"), () =>
        {
            var json = McpController.Shared.ClientConfigJson();
            if (json != null && ClipboardImage.SetText(json))
                GlassConfirmBadge.Show(Tr.L("toast_mcp_config_copied", "Config MCP copiée"));
        });
        _mcpRegenerate = ButtonFor(Tr.L("mcp_regenerate"), () =>
        {
            McpController.Shared.RegenerateToken();
            GlassConfirmBadge.Show(Tr.L("toast_token_regenerated", "Jeton régénéré"), "key.fill");
        });
        _mcpRegenerate.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        buttons.Children.Add(_mcpCopy);
        buttons.Children.Add(_mcpRegenerate);

        UpdateMcpStatus(McpController.Shared.Status);
        var section = Section(Tr.L("sec_mcp"), toggle, status, buttons);
        var footer = new TextBlock { Text = Tr.L("mcp_footer"), Style = (Style)_owner.FindResource("FL.FooterText") };
        ((StackPanel)section).Children.Add(footer);
        return section;
    }

    public void UpdateMcpStatus(McpStatus status)
    {
        if (_mcpStatusText == null || _mcpDot == null) return;
        _mcpStatusText.Text = status.Kind switch
        {
            McpStatusKind.Starting => Tr.L("mcp_status_starting"),
            McpStatusKind.Running => Tr.F("mcp_status_running", $"127.0.0.1:{status.Port}"),
            McpStatusKind.Failed => Tr.F("mcp_status_failed", status.Reason ?? ""),
            _ => Tr.L("mcp_status_stopped"),
        };
        if (status.Kind == McpStatusKind.Running) _mcpDot.Fill = Green;
        else if (status.Kind == McpStatusKind.Failed) _mcpDot.Fill = Red;
        else _mcpDot.SetResourceReference(Shape.FillProperty, "FL.TextSecondary");
        if (_mcpCopy != null) _mcpCopy.IsEnabled = status.IsRunning;
        if (_mcpRegenerate != null) _mcpRegenerate.IsEnabled = status.IsRunning;
    }

    // MARK: - Capture (After capture · Image format · save folder)

    public UIElement CapturePane()
    {
        _rebuildCurrent = null;
        var p = Preferences.Shared;
        var dependsOnPreview = new List<UIElement>();
        void SetPreviewDependents(bool on) { foreach (var e in dependsOnPreview) e.IsEnabled = on; }

        var corner = Picker(Tr.L("preview_corner"), new[]
        {
            ("bottomLeft", Tr.L("corner_bottom_left")), ("topLeft", Tr.L("corner_top_left")),
            ("bottomRight", Tr.L("corner_bottom_right")), ("topRight", Tr.L("corner_top_right")),
        }, p.PreviewCorner, v => Write(() => p.PreviewCorner = v));
        var follow = Toggle(Tr.L("preview_follow_cursor"), p.PreviewFollowsCursor, on => Write(() => p.PreviewFollowsCursor = on));
        var closeAfterDrag = Toggle(Tr.L("close_preview_after_drag"), p.ClosePreviewAfterDrag, on => Write(() => p.ClosePreviewAfterDrag = on));
        dependsOnPreview.AddRange(new UIElement[] { corner, follow, closeAfterDrag });
        var showPreview = Toggle(Tr.L("show_preview"), p.ShowPreview, on => { Write(() => p.ShowPreview = on); SetPreviewDependents(on); });

        var soundIds = new List<(string, string)>();
        foreach (var id in CaptureSound.CaptureSoundIds) soundIds.Add((id, CaptureSound.MenuLabel(id)));
        var sound = Picker(Tr.L("sound_capture"), soundIds, p.CaptureSoundName, id =>
        {
            Write(() => p.CaptureSoundName = id);
            CaptureSound.PreviewCapture(id);                 // preview: the user hears it
        });
        var playSound = Toggle(Tr.L("play_sound"), p.PlaySound, on => { Write(() => p.PlaySound = on); sound.IsEnabled = on; });
        sound.IsEnabled = p.PlaySound;
        SetPreviewDependents(p.ShowPreview);

        var root = new StackPanel();
        root.Children.Add(Section(Tr.L("sec_after_capture"),
            showPreview, corner, follow, closeAfterDrag,
            Toggle(Tr.L("copy_clipboard"), p.CopyToClipboard, on => Write(() => p.CopyToClipboard = on)),
            Toggle(Tr.L("open_editor"), p.OpenEditor, on => Write(() => p.OpenEditor = on)),
            playSound, sound));
        root.Children.Add(Section(Tr.L("sec_capture"),
            Picker(Tr.L("image_format"), new[] { ("png", "PNG"), ("jpg", "JPEG") }, p.ImageFormat == "jpg" ? "jpg" : "png",
                v => Write(() => p.ImageFormat = v)),
            SaveFolderRow()));
        _rebuildCurrent = () => Swap(CapturePane);
        return root;
    }

    /// Windows addition: the save folder (the macOS app only had it as a hidden default).
    private UIElement SaveFolderRow()
    {
        var path = new TextBlock
        {
            Text = Preferences.Shared.EnsureSaveDirectory(), TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 210, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        path.ToolTip = path.Text;
        path.SetResourceReference(TextBlock.ForegroundProperty, "FL.TextSecondary");
        var choose = ButtonFor(Tr.L("choose_folder", "Choose…"), () =>
        {
            var dialog = new OpenFolderDialog { InitialDirectory = Preferences.Shared.EnsureSaveDirectory(), Multiselect = false };
            if (dialog.ShowDialog(_owner) != true) return;
            Write(() => Preferences.Shared.SaveDirectory = dialog.FolderName);
            path.Text = Preferences.Shared.EnsureSaveDirectory();
            path.ToolTip = path.Text;
            CaptureLibrary.Shared.Reload();
        });
        var value = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        value.Children.Add(path);
        value.Children.Add(choose);
        return LabeledRow(Tr.L("save_folder", "Save captures to"), value);
    }

    // MARK: - Recording

    public UIElement RecordingPane()
    {
        _rebuildCurrent = null;
        var p = Preferences.Shared;
        var micHint = MicrophoneHint();
        micHint.Visibility = p.RecordMicrophone && !MicrophoneAccess.IsAllowed ? Visibility.Visible : Visibility.Collapsed;

        var soundIds = new List<(string, string)>();
        foreach (var id in CaptureSound.RecordSoundIds) soundIds.Add((id, CaptureSound.MenuLabel(id)));
        var sound = Picker(Tr.L("sound_recording_start"), soundIds, p.RecordSoundName, id =>
        {
            Write(() => p.RecordSoundName = id);
            CaptureSound.PreviewRecording(id);
        });
        sound.IsEnabled = p.PlaySound;

        var root = new StackPanel();
        root.Children.Add(Section(Tr.L("sec_recording"),
            Toggle(Tr.L("record_system_audio"), p.RecordSystemAudio, on => Write(() => p.RecordSystemAudio = on)),
            Toggle(Tr.L("record_microphone"), p.RecordMicrophone, on =>
            {
                Write(() => p.RecordMicrophone = on);
                // No runtime prompt on Windows: when desktop apps are denied the microphone, say where to allow it.
                micHint.Visibility = on && !MicrophoneAccess.IsAllowed ? Visibility.Visible : Visibility.Collapsed;
            }),
            micHint,
            sound));
        _rebuildCurrent = () => Swap(RecordingPane);
        return root;
    }

    private FrameworkElement MicrophoneHint()
    {
        var text = new TextBlock { Text = Tr.L("mic_permission_windows"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 6) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FL.TextSecondary");
        var open = ButtonFor(Tr.L("open_windows_settings"), MicrophoneAccess.OpenSettings);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 0, 0, 8);
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(open);
        return panel;
    }

    // MARK: - About (logo, version, tagline, update button)

    public UIElement AboutPane()
    {
        _rebuildCurrent = null;
        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 14, 0, 0) };
        var logo = new Image { Width = 64, Height = 64, Margin = new Thickness(0, 14, 0, 9), Source = AppIconImage(128) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        root.Children.Add(logo);
        root.Children.Add(Centered(new TextBlock { Text = "FrameLine", FontSize = 20, FontWeight = FontWeights.Bold }));
        root.Children.Add(Secondary(Centered(new TextBlock { Text = $"{Tr.L("version")} {AppPaths.Version}", Margin = new Thickness(0, 9, 0, 0) })));
        root.Children.Add(Secondary(Centered(new TextBlock
        {
            Text = Tr.L("about_tagline"), FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 9, 0, 0),
        })));
        var update = ButtonFor(Tr.L("check_updates"), Dialogs.ShowUpToDate);
        update.FontSize = 12;
        update.Padding = new Thickness(10, 2, 10, 2);
        update.HorizontalAlignment = HorizontalAlignment.Center;
        update.Margin = new Thickness(0, 17, 0, 0);
        root.Children.Add(update);

        var copyright = Centered(new TextBlock { Text = "© 2026 SAAS Empire", FontSize = 11 });
        copyright.SetResourceReference(TextBlock.ForegroundProperty, "FL.TextTertiary");
        var dock = new DockPanel { LastChildFill = true, MinHeight = 540 };
        DockPanel.SetDock(copyright, Dock.Bottom);
        copyright.Margin = new Thickness(0, 0, 0, 12);
        dock.Children.Add(copyright);
        dock.Children.Add(root);
        return dock;
    }

    internal static ImageSource? AppIconImage(int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/Assets/frameline-256.png");
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is System.IO.IOException or NotSupportedException) { return null; }
    }

    // MARK: - Building blocks

    private void Swap(Func<UIElement> build)
    {
        if (_owner is SettingsWindow && _owner.FindName("PaneHost") is ScrollViewer host)
        {
            double offset = host.VerticalOffset;
            host.Content = build();
            host.ScrollToVerticalOffset(offset);
        }
    }

    private UIElement Section(string header, params UIElement[] rows)
    {
        var list = new StackPanel();
        bool first = true;
        foreach (var row in rows)
        {
            if (!first && row is not StackPanel { Tag: "noSeparator" })
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "FL.Separator");
                // The separator follows its row's visibility (the microphone hint can be collapsed).
                if (row is FrameworkElement fe)
                    line.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = fe });
                list.Children.Add(line);
            }
            list.Children.Add(row);
            first = false;
        }
        var card = new Border { Child = list, Style = (Style)_owner.FindResource("FL.SectionCard") };
        var section = new StackPanel();
        section.Children.Add(new TextBlock { Text = header, Style = (Style)_owner.FindResource("FL.SectionHeader") });
        section.Children.Add(card);
        return section;
    }

    private CheckBox Toggle(string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            IsChecked = value, Style = (Style)_owner.FindResource("FL.ToggleSwitch"),
        };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    private FrameworkElement Picker(string label, IEnumerable<(string Tag, string Text)> items, string selected, Action<string> changed)
    {
        var combo = new ComboBox { Style = (Style)_owner.FindResource("FL.ComboBox"), MinWidth = 170, MaxWidth = 260 };
        ComboBoxItem? first = null, match = null;
        foreach (var (tag, text) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            combo.Items.Add(item);
            first ??= item;
            if (tag == selected) match = item;
        }
        combo.SelectedItem = match ?? first;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string tag }) changed(tag);
        };
        return LabeledRow(label, combo);
    }

    private static FrameworkElement LabeledRow(string label, FrameworkElement value)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), FontSize = 13 };
        grid.Children.Add(text);
        Grid.SetColumn(value, 1);
        value.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(value);
        return grid;
    }

    private Button ButtonFor(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)_owner.FindResource("FL.Button") };
        b.Click += (_, _) => click();
        return b;
    }

    private static TextBlock Centered(TextBlock t)
    {
        t.HorizontalAlignment = HorizontalAlignment.Center;
        return t;
    }

    private static TextBlock Secondary(TextBlock t)
    {
        t.SetResourceReference(TextBlock.ForegroundProperty, "FL.TextSecondary");
        return t;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

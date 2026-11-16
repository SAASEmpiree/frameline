using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using FrameLine.Core;
using FrameLine.Core.Mcp;
using FrameLine.Platform;

namespace FrameLine.Shell;

/// Port of PreferencesWindowController.swift + SettingsView.swift: a preference toolbar (icon over
/// label) with 4 short tabs (General, Capture, Recording, About). Like the macOS window, it follows
/// the SYSTEM light/dark mode, not the app theme, and relabels itself live when the display
/// language changes.
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _shared;
    /// Current tab ("settingsTab"), kept for the session like the macOS AppStorage value.
    private static string _tab = "general";

    private static readonly (string Id, string Key, string Glyph)[] Tabs =
    {
        ("general", "tab_general", "\uE713"),
        ("capture", "sec_capture", "\uE722"),
        ("recording", "sec_recording", "\uE7C8"),
        ("about", "tab_about", "\uE946"),
    };

    private readonly SettingsPanes _panes;
    private readonly Action _onLanguage;
    private readonly Action _onSystemTheme;
    private readonly Action<string> _onPreference;
    private readonly Action<McpStatus> _onMcpStatus;

    /// Opens (or brings back) the Settings window on the active monitor, optionally on a given tab.
    public static void Present(string? tab = null)
    {
        if (tab != null) _tab = tab;
        if (_shared == null)
        {
            _shared = new SettingsWindow();
            _shared.Closed += (_, _) => _shared = null;
            _shared.ShowCenteredOnActiveScreen();
        }
        else
        {
            _shared.ShowTab(_tab);
            if (_shared.WindowState == WindowState.Minimized) _shared.WindowState = WindowState.Normal;
            _shared.CenterOnActiveScreen();
        }
        _shared.Activate();
        _shared.Topmost = true;      // brings it above a full-screen app, then back to normal
        _shared.Topmost = false;
        _shared.Focus();
    }

    private SettingsWindow()
    {
        InitializeComponent();
        _panes = new SettingsPanes(this);
        ApplySystemAppearance();
        Glass.TryApplyBackdrop(this, Backdrop.Mica);

        _onLanguage = () => Dispatcher.BeginInvoke(() => { BuildToolbar(); ShowTab(_tab); });
        _onSystemTheme = ApplySystemAppearance;
        // Our own writes echo synchronously through Changed: ignore them, refresh only for outside changes.
        _onPreference = name => { if (!_panes.IsWriting) Dispatcher.BeginInvoke(() => _panes.Refresh(name)); };
        _onMcpStatus = status => Dispatcher.BeginInvoke(() => _panes.UpdateMcpStatus(status));
        I18n.Shared.LanguageChanged += _onLanguage;
        ThemeManager.SystemThemeChanged += _onSystemTheme;
        Preferences.Shared.Changed += _onPreference;
        McpController.Shared.StatusChanged += _onMcpStatus;
        Closed += (_, _) =>
        {
            I18n.Shared.LanguageChanged -= _onLanguage;
            ThemeManager.SystemThemeChanged -= _onSystemTheme;
            Preferences.Shared.Changed -= _onPreference;
            McpController.Shared.StatusChanged -= _onMcpStatus;
        };

        BuildToolbar();
        ShowTab(_tab);
    }

    private void ApplySystemAppearance()
    {
        bool dark = ThemeManager.SystemIsDark;
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(ThemeManager.CreatePalette(dark));
        Glass.SetDarkTitleBar(this, dark);
    }

    private void BuildToolbar()
    {
        Toolbar.Children.Clear();
        foreach (var (id, key, glyph) in Tabs)
        {
            var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            label.Children.Add(new TextBlock
            {
                Text = glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = (FontFamily)FindResource("FL.IconFont"), Margin = new Thickness(0, 2, 0, 3),
            });
            label.Children.Add(new TextBlock { Text = Tr.L(key), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            var button = new RadioButton
            {
                GroupName = "settingsTabs", Tag = id, Content = label, MinWidth = 72,
                Margin = new Thickness(2, 0, 2, 0), Template = ToolbarItemTemplate(), Cursor = System.Windows.Input.Cursors.Hand,
                IsChecked = id == _tab,
            };
            button.Checked += (s, _) => ShowTab((string)((RadioButton)s!).Tag);
            Toolbar.Children.Add(button);
        }
    }

    private void ShowTab(string id)
    {
        _tab = id;
        foreach (var child in Toolbar.Children)
            if (child is RadioButton rb) rb.IsChecked = (string)rb.Tag == id;
        PaneHost.Content = id switch
        {
            "capture" => _panes.CapturePane(),
            "recording" => _panes.RecordingPane(),
            "about" => _panes.AboutPane(),
            _ => _panes.GeneralPane(),
        };
        PaneHost.ScrollToTop();
    }

    /// Toolbar item: rounded highlight behind the selected tab, accent-coloured icon and label.
    private static ControlTemplate ToolbarItemTemplate()
    {
        var t = new ControlTemplate(typeof(RadioButton));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetResourceReference(TextElement.ForegroundProperty, "FL.TextSecondary");
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(content);
        t.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension("FL.TextPrimary"), "Bd"));
        t.Triggers.Add(hover);
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("FL.ControlHoverBackground"), "Bd"));
        checkedTrigger.Setters.Add(new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension("FL.Accent"), "Bd"));
        t.Triggers.Add(checkedTrigger);
        return t;
    }
}

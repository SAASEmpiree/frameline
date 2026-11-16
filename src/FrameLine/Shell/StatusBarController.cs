using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FrameLine.Core;
using FrameLine.Platform;
using FrameLine.Recording;
using Forms = System.Windows.Forms;

namespace FrameLine.Shell;

/// Port of StatusBarController.swift: the tray icon (menu-bar item) and its menu, same items, same
/// order, same separators, labels from I18n, rebuilt live when the display language changes.
///
/// The menu is a WPF ContextMenu (styled FL.ContextMenu in Theme.xaml) rather than a WinForms
/// ContextMenuStrip: it shares the app's light/dark palette and fonts, does per-monitor DPI
/// natively, shows right-aligned shortcut hints with InputGestureText, and needs no custom renderer.
/// The one tray-specific step is giving its popup the foreground (OnMenuOpened) so a click
/// elsewhere closes it.
public sealed class StatusBarController : IDisposable
{
    public static StatusBarController? Current { get; private set; }

    private readonly Forms.NotifyIcon _tray;
    private TrayIcons _icons;
    private ContextMenu _menu = new();
    private MenuItem? _recordItem;
    private MenuItem? _pinItem;
    private readonly DispatcherTimer _recTimer;
    private bool _pulseDim;
    private Action? _balloonClick;

    public Action? OnRegion, OnWindow, OnFull, OnToggleRecord, OnPreferences, OnAbout;
    public Action? OnAllCaptures, OnCaptureHistory, OnSetupGuide, OnCheckUpdates, OnPinFromClipboard, OnQuit;
    public Action<int>? OnFullDelayed;

    public StatusBarController()
    {
        _icons = TrayIcons.Create();
        _tray = new Forms.NotifyIcon { Icon = _icons.Idle, Text = Tooltip(), Visible = true };
        _tray.MouseUp += OnTrayMouseUp;
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _tray.BalloonTipClosed += (_, _) => _balloonClick = null;

        // Pulses the recording glyph (1 ↔ 0.4 every 0.75 s, like the macOS button) and refreshes the
        // elapsed time in the tooltip.
        _recTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.75) };
        _recTimer.Tick += (_, _) => PulseTick();

        BuildMenu();
        Current = this;

        I18n.Shared.LanguageChanged += () => Ui(() => { BuildMenu(); _tray.Text = Tooltip(); });
        RecordingManager.Shared.StateChanged += () => Ui(UpdateRecordState);
        ThemeManager.SystemThemeChanged += RebuildIcons;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Ui(RebuildIcons);
    }

    // MARK: - Menu

    private void BuildMenu()
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("FL.ContextMenu"), Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Opened += OnMenuOpened;

        // Product header "FrameLine <version>" (no account block: FrameLine is 100 % local).
        var header = new MenuItem
        {
            Header = new TextBlock { Text = "FrameLine " + AppPaths.Version, FontSize = 11, FontWeight = FontWeights.SemiBold },
            IsHitTestVisible = false, Focusable = false,
        };
        header.SetResourceReference(Control.ForegroundProperty, "FL.TextSecondary");
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item(Tr.L("m_capture_area"), "\uE7A8", () => OnRegion?.Invoke(), CaptureHotkey.Label(CaptureHotkey.Region)));
        menu.Items.Add(Item(Tr.L("m_capture_window"), "\uE922", () => OnWindow?.Invoke(), CaptureHotkey.Label(CaptureHotkey.Window)));
        menu.Items.Add(Item(Tr.L("m_capture_fullscreen"), "\uE740", () => OnFull?.Invoke(), CaptureHotkey.Label(CaptureHotkey.FullScreen)));
        menu.Items.Add(new Separator());

        _recordItem = Item(Tr.L("m_start_recording"), "\uE7C8", () => OnToggleRecord?.Invoke(), CaptureHotkey.Label(CaptureHotkey.Record));
        menu.Items.Add(_recordItem);
        menu.Items.Add(new Separator());

        var delayed = Item(Tr.L("m_capture_fullscreen_in"), "\uE916", null);
        foreach (var s in new[] { 3, 5, 10 })
        {
            int secs = s;
            delayed.Items.Add(new MenuItem { Header = Tr.F("m_seconds", secs), Command = new Relay(() => OnFullDelayed?.Invoke(secs)) });
        }
        menu.Items.Add(delayed);
        _pinItem = Item(Tr.L("m_pin_clipboard"), "\uE718", () => OnPinFromClipboard?.Invoke());
        menu.Items.Add(_pinItem);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item(Tr.L("m_capture_history"), "\uE81C", () => OnCaptureHistory?.Invoke()));
        menu.Items.Add(Item(Tr.L("m_all_captures"), "\uE8F1", () => OnAllCaptures?.Invoke()));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item(Tr.L("m_setup_guide"), "\uE8FD", () => OnSetupGuide?.Invoke()));
        menu.Items.Add(Item(Tr.L("m_check_updates"), "\uE895", () => OnCheckUpdates?.Invoke()));
        menu.Items.Add(Item(Tr.L("m_settings"), "\uE713", () => OnPreferences?.Invoke(), CaptureHotkey.SettingsHint));
        // Quit: same section as Settings (no separator), like the macOS menu.
        menu.Items.Add(Item(Tr.L("m_quit"), "\uE8BB", () => OnQuit?.Invoke(), CaptureHotkey.QuitHint));

        if (_menu.IsOpen) _menu.IsOpen = false;
        _menu.Opened -= OnMenuOpened;
        _menu = menu;
        UpdateRecordState();
    }

    private static MenuItem Item(string title, string glyph, Action? action, string? hint = null)
    {
        var item = new MenuItem
        {
            Header = title,
            Icon = new TextBlock
            {
                Text = glyph, FontSize = 14,
                FontFamily = (FontFamily)Application.Current.FindResource("FL.IconFont"),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (hint != null) item.InputGestureText = hint;
        if (action != null) item.Command = new Relay(action);
        return item;
    }

    private void UpdateRecordState()
    {
        bool on = RecordingManager.Shared.IsRecording;
        if (_recordItem != null)
        {
            _recordItem.Header = Tr.L(on ? "m_stop_recording" : "m_start_recording");
            if (_recordItem.Icon is TextBlock t) t.Text = on ? "\uE71A" : "\uE7C8";
        }
        if (on)
        {
            _pulseDim = false;
            _tray.Icon = _icons.Recording;
            if (!_recTimer.IsEnabled) _recTimer.Start();
        }
        else
        {
            _recTimer.Stop();
            _tray.Icon = _icons.Idle;
        }
        _tray.Text = Tooltip();
    }

    private void PulseTick()
    {
        if (!RecordingManager.Shared.IsRecording) { UpdateRecordState(); return; }
        _pulseDim = !_pulseDim;
        _tray.Icon = _pulseDim ? _icons.RecordingDim : _icons.Recording;
        _tray.Text = Tooltip();
    }

    private static string Tooltip()
    {
        string text;
        if (RecordingManager.Shared.IsRecording)
        {
            var e = RecordingManager.Shared.Elapsed;
            string clock = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
            text = $"FrameLine — {Tr.L("sec_recording")} {clock}";
        }
        else text = Tr.L("tray_tooltip", "FrameLine");
        return text.Length > 127 ? text[..127] : text;   // NOTIFYICONDATA.szTip limit
    }

    private void OnTrayMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left && e.Button != Forms.MouseButtons.Right) return;
        ShowMenu();
    }

    public void ShowMenu()
    {
        // validateMenuItem: "Pin Image from Clipboard" is only enabled with an image on the clipboard.
        if (_pinItem != null) _pinItem.IsEnabled = ClipboardImage.HasImage();
        _menu.IsOpen = true;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        // Without the foreground, a tray-opened popup never sees the click outside it and stays open.
        if (PresentationSource.FromVisual(_menu) is HwndSource src) Native.SetForegroundWindow(src.Handle);
        _menu.Focus();
    }

    private void RebuildIcons()
    {
        var old = _icons;
        _icons = TrayIcons.Create();
        _tray.Icon = RecordingManager.Shared.IsRecording ? _icons.Recording : _icons.Idle;
        old.Dispose();
    }

    // MARK: - Balloon notifications (Notifier)

    public void ShowBalloon(string title, string text, Action? onClick)
    {
        _balloonClick = onClick;
        _tray.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);
    }

    private static void Ui(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    public void Dispose()
    {
        _recTimer.Stop();
        _menu.IsOpen = false;
        _tray.Visible = false;
        _tray.Dispose();
        _icons.Dispose();
        if (Current == this) Current = null;
    }

    /// Minimal ICommand so MenuItem clicks run after the menu closed (no re-entrancy with the popup).
    private sealed class Relay : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public Relay(Action action) => _action = action;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) =>
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, _action);
    }
}

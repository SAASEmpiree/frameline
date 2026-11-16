using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FrameLine.Core;
using FrameLine.Platform;

namespace FrameLine.Shell;

/// Port of OnboardingWindowController.swift + OnboardingView.swift: the first-launch setup guide
/// (Welcome · Microphone (optional) · Shortcuts · All set), always dark, 660×640. Closing it with
/// the window's close button counts as finishing, like the macOS red button.
public sealed class OnboardingWindow : Window
{
    private static OnboardingWindow? _shared;

    private const int LastStep = 3;
    private int _step;
    private bool _didComplete;
    private Action? _onComplete;
    private readonly OnboardingSteps _steps;
    private readonly Border _card;
    private readonly Button _back;
    private readonly StackPanel _dots;
    private readonly ContentControl _next;
    private readonly DispatcherTimer _poll;

    /// Opens the guide (a fresh one at step 1 if it was closed) on the active monitor.
    public static void Present(Action? onComplete)
    {
        if (_shared == null)
        {
            _shared = new OnboardingWindow();
            _shared.Closed += (_, _) => _shared = null;
        }
        _shared._onComplete = onComplete;
        _shared.ShowCenteredOnActiveScreen();
        if (_shared.WindowState == WindowState.Minimized) _shared.WindowState = WindowState.Normal;
        _shared.Activate();
    }

    private OnboardingWindow()
    {
        Title = "";
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = true;
        Background = OnboardingSteps.WindowBackground;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        Icon = SettingsPanes.AppIconImage(64);
        // Always dark, whatever the app theme (preferredColorScheme(.dark)).
        Resources.MergedDictionaries.Add(ThemeManager.CreatePalette(dark: true));
        Glass.SetDarkTitleBar(this, true);
        Glass.SetCaptionColor(this, Color.FromRgb(0x12, 0x12, 0x12), Color.FromRgb(0x2A, 0x2A, 0x2A));
        AlwaysActiveWindow.Attach(this);

        _steps = new OnboardingSteps(
            onStartCapture: FinishCapturing,
            onFinish: Finish);
        _steps.SetRebuild(() => GoTo(_step, animate: false));

        var root = new Grid { Width = 660, Height = 640 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });   // the card sits ~1/3 from the top
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = TopChrome();
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        _card = new Border
        {
            Margin = new Thickness(26, 12, 26, 12), Padding = new Thickness(32), CornerRadius = new CornerRadius(16),
            Background = OnboardingSteps.CardFill, BorderBrush = OnboardingSteps.CardStroke, BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 52, ShadowDepth = 14, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        Grid.SetRow(_card, 2);
        root.Children.Add(_card);

        _back = OnboardingSteps.PlainButton("‹ Back", 12);
        _back.Click += (_, _) => GoTo(Math.Max(0, _step - 1));
        _dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _next = new ContentControl { HorizontalAlignment = HorizontalAlignment.Right, Focusable = false };
        var nav = new Grid { Margin = new Thickness(26, 18, 26, 18) };
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        nav.Children.Add(_back);
        Grid.SetColumn(_dots, 1);
        nav.Children.Add(_dots);
        Grid.SetColumn(_next, 2);
        nav.Children.Add(_next);
        Grid.SetRow(nav, 4);
        root.Children.Add(nav);

        Content = root;

        // The microphone state can change in Windows Settings while the guide is open (macOS polled
        // the screen-recording permission every second the same way).
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => _steps.RefreshPermission();
        _poll.Start();
        Closed += (_, _) => _poll.Stop();

        GoTo(0, animate: false);
    }

    private FrameworkElement TopChrome()
    {
        var bar = new DockPanel { Margin = new Thickness(26, 20, 26, 4), LastChildFill = false };
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(OnboardingSteps.BrandMark(20));
        brand.Children.Add(new TextBlock { Text = "FrameLine", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(brand, Dock.Left);
        bar.Children.Add(brand);
        var skip = OnboardingSteps.PlainButton("Skip Setup", 12);
        skip.Click += (_, _) => Finish();
        DockPanel.SetDock(skip, Dock.Right);
        bar.Children.Add(skip);
        return bar;
    }

    private void GoTo(int step, bool animate = true)
    {
        _step = Math.Clamp(step, 0, LastStep);
        var content = _steps.Build(_step);
        _card.Child = content;
        if (animate)
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });

        _back.Visibility = _step > 0 ? Visibility.Visible : Visibility.Hidden;
        _back.IsEnabled = _step > 0;

        _dots.Children.Clear();
        for (int i = 0; i <= LastStep; i++)
        {
            var dot = new Border
            {
                Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(i == 0 ? 0 : 7, 0, 0, 0),
                Width = i == _step ? 18 : 7,
                Background = i == _step ? Brushes.White : OnboardingSteps.White(0.15),
            };
            _dots.Children.Add(dot);
        }

        // The last step carries its actions inside the card: no "Continue" there.
        if (_step < LastStep)
        {
            var next = OnboardingSteps.Pill(_step == 0 ? "Get Started" : "Continue", filled: true, glyph: "\uE72A");
            next.IsDefault = true;           // Return = default action
            next.Click += (_, _) => GoTo(_step + 1);
            _next.Content = next;
        }
        else
        {
            _next.Content = new Border { Width = 96, Height = 1 };
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { e.Handled = true; Finish(); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        Complete();      // the close button equals "finish" (else the guide replays and nothing opens)
    }

    private void Finish()
    {
        Complete();
        Close();
    }

    /// "Take Your First Screenshot": finishes (opens the library) then starts an area capture.
    /// The capture waits for the window close animation, or the guide would be in the frozen shot.
    private void FinishCapturing()
    {
        Complete();
        Close();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { timer.Stop(); AppCoordinator.Shared.CaptureRegion(); };
        timer.Start();
    }

    private void Complete()
    {
        if (_didComplete) return;
        _didComplete = true;
        Preferences.Shared.OnboardingCompleted = true;
        _onComplete?.Invoke();
    }
}

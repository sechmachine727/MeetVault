using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using MeetVault.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT;

namespace MeetVault.Views;

public sealed partial class OverlayWindow : Window
{
    private const double WidthDip = 1100;
    private const double HeightDip = 220;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    // DWM draws a 1px frame (and shadow) around an Overlapped window even with
    // the content extended into the title bar; DWMWA_COLOR_NONE suppresses it.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    private readonly OverlappedPresenter _presenter;
    private readonly double _scale;
    private Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController? _acrylic;
    // Veil is code-built, not XAML: this file's XamlCompiler rejects new direct
    // children of the root Grid (bisected: Border and Rectangle both exit 1).
    // The Backdrop slider is code-built too: x:Bind TwoWay inside Button.Flyout
    // never pushed values back to Vm.Opacity (proven: identical pixels at 1.0/0.3).
    private readonly SolidColorBrush _veil = new(Colors.Black);
    private readonly Slider _backdropSlider = new() { Minimum = 0, Maximum = 1, StepFrequency = 0.05 };
    // Conversation view: a bottom-anchored stack of recent lines (latest big and
    // bold). Code-built — XamlCompiler 1.8 rejects new direct children of the
    // root Grid in XAML; Border AND Rectangle both bisected exit-1.
    private readonly StackPanel _conversationPanel = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 6,
        VerticalAlignment = VerticalAlignment.Bottom,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(24, 0, 24, 12),
    };
    private readonly TextBlock _noLines = new()
    {
        Text = "No transcript lines yet.",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.7,
    };
    // One cogwheel; every overlay setting lives in its flyout (owner-requested
    // consolidation — nothing is scattered in the header/footer anymore).
    private readonly Button _settingsButton = new()
    {
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(8, 4, 8, 4),
        VerticalAlignment = VerticalAlignment.Center,
        Content = new FontIcon { Glyph = "\uE713", FontSize = 16 },
    };
    private readonly TextBox _renameBox = new();
    private readonly Flyout _renameFlyout;
    private TranscriptLine? _renaming;

    public OverlayWindow()
    {
        Vm = new OverlayViewModel();
        InitializeComponent();
        SeedFromSettings();
        _veil.Opacity = Vm.Opacity;
        // Plain see-through: no material, no blur, no tint. AcrylicBrush is
        // in-app only, DesktopAcrylic tints+blurs, and the HWND tricks
        // (LWA_COLORKEY, WCA_ACCENT_POLICY) are ignored on this runtime — so
        // the window backdrop is a fully transparent composition brush and the
        // DWM frame is suppressed. At zero darkness only the content remains;
        // the black veil is the user's depth control and paints under content.
        var veilHost = new Grid { Background = _veil };
        Grid.SetRowSpan(veilHost, 3);
        RootSurface.Children.Insert(0, veilHost);
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.Opacity)) _veil.Opacity = Vm.Opacity;
            if (e.PropertyName is nameof(OverlayViewModel.FontSize) or nameof(OverlayViewModel.FontFamily) or nameof(OverlayViewModel.ViewLines)) BuildConversation();
            if (e.PropertyName == nameof(OverlayViewModel.IsPinned) && _presenter is not null) _presenter.IsAlwaysOnTop = Vm.IsPinned;
        };
        // Same ABI rule as Live/Details: ObservableCollection NREs in ComWrappers
        // vtable computation in the trimmed exe. Array snapshot, refreshed on change.
        TranscriptList.ItemsSource = Vm.Lines.ToArray();
        // Conversation mode replaces the history list. History buffer stays intact.
        AutomationProperties.SetName(_settingsButton, "Overlay settings");
        ToolTipService.SetToolTip(_settingsButton, "Overlay settings");
        _settingsButton.Flyout = BuildSettingsFlyout();
        Grid.SetColumn(_settingsButton, 2);
        HeaderBar.Children.Add(_settingsButton);
        // NOTE: visual-parent lookup is null in the ctor (proven twice by
        // crash-log NRE) — the tree is only attached at Loaded. Host the
        // conversation panel then; text state is already correct via BuildConversation.
        RootSurface.Loaded += (_, _) =>
        {
            TranscriptList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            ((Grid)VisualTreeHelper.GetParent(TranscriptList)).Children.Add(_conversationPanel);
            // Consolidation: the old scattered controls go away — Pin + Backdrop
            // in the header, Font size + Lines in the footer all live in the
            // cogwheel flyout now.
            if (HeaderBar.Children.Count > 1 && HeaderBar.Children[1] is FrameworkElement pinToggle) pinToggle.Visibility = Visibility.Collapsed;
            BackdropButton.Visibility = Visibility.Collapsed;
            foreach (var child in RootSurface.Children)
                if (child is Grid footer && Grid.GetRow(footer) == 2) footer.Visibility = Visibility.Collapsed;
        };
        _renameFlyout = BuildRenameFlyout();
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        _scale = scale;
        AppWindow.Resize(new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale)));
        // Dock bottom-center like a YouTube subtitle. WorkArea excludes the
        // taskbar; AppWindow geometry is physical pixels, hence the scale.
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        if (workArea is not null)
        {
            var wa = workArea.Value;
            var (px, py) = CaptionPlacement.BottomCenter(wa.X, wa.Y, wa.Width, wa.Height, (int)(WidthDip * scale), (int)(HeightDip * scale), (int)(24 * scale));
            AppWindow.Move(new PointInt32(px, py));
        }
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            // Transparent chrome: content extends into the title bar, the OS keeps
            // Close/Min/Max over the reserved header area, nothing else is drawn.
            var bar = AppWindow.TitleBar;
            bar.ExtendsContentIntoTitleBar = true;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        // Applied after the window loads: a ctor-set backdrop is dropped when
        // the presenter / title bar are configured (probe 2026-10-10 — a
        // ctor-set backdrop left the window at the default theme grey, proven
        // with a pure-green background directly behind the strip).
        RootSurface.Loaded += (_, _) =>
        {
            // WinUI 3 only composites the desktop through a *material*: a plain
            // colour-brush backdrop renders opaque black (proven with a pure-green
            // window behind the strip), and all HWND tricks are ignored. So the
            // see-through comes from Desktop Acrylic with tint and luminosity
            // turned off and the thin variant — at zero darkness the strip is
            // essentially the desktop behind it, and the veil adds the darkness.
            // Applied after load: a backdrop set in the ctor is dropped.
            var supportsAcrylic = Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
            if (supportsAcrylic)
            {
                _acrylic = new Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController
                {
                    Kind = Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicKind.Thin,
                    TintColor = Colors.Black,
                    TintOpacity = 0f,
                    LuminosityOpacity = 0f,
                    FallbackColor = Colors.Black,
                };
                _acrylic.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
                _acrylic.SetSystemBackdropConfiguration(
                    new Microsoft.UI.Composition.SystemBackdrops.SystemBackdropConfiguration { IsInputActive = true });
            }
            else
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
            SuppressWindowFrame(hwnd);
            UpdateDragRects();
        };
        RootSurface.SizeChanged += (_, _) => UpdateDragRects();
        _presenter = (OverlappedPresenter)AppWindow.Presenter;
        _presenter.IsAlwaysOnTop = Vm.IsPinned;
        ((INotifyCollectionChanged)Vm.Lines).CollectionChanged += OnLinesChanged;
        LiveBridge.LineAdded += OnLiveLine;
        LiveBridge.Cleared += OnLiveCleared;
        LiveBridge.Renamed += OnRenamed;
        Closed += (_, _) => { LiveBridge.LineAdded -= OnLiveLine; LiveBridge.Cleared -= OnLiveCleared; LiveBridge.Renamed -= OnRenamed; _acrylic?.Dispose(); };
        foreach (var (speaker, text) in LiveBridge.Recent()) Vm.AddLine(speaker, text); // catch up mid-meeting
        RefreshListState();
        BuildConversation();
    }

    // Settings -> Overlay: the controls persist these, so the window must honor them.
    private void SeedFromSettings()
    {
        try
        {
            var repo = AppStore.Repo;
            Vm.IsPinned = repo.GetSetting("overlay.pin") != "off";
            if (double.TryParse(repo.GetSetting("overlay.opacity"), out var op)) Vm.Opacity = op;
            if (double.TryParse(repo.GetSetting("overlay.font"), out var font)) Vm.FontSize = font;
            if (double.TryParse(repo.GetSetting("overlay.lines"), out var lines)) Vm.LineCount = lines;
            if (double.TryParse(repo.GetSetting("overlay.viewlines"), out var viewLines)) Vm.ViewLines = viewLines;
            var family = repo.GetSetting("overlay.fontfamily");
            if (!string.IsNullOrWhiteSpace(family)) Vm.FontFamily = family;
        }
        catch { }
    }

    private void OnLiveLine(string speaker, string text)
        => DispatcherQueue.TryEnqueue(() => Vm.AddLine(speaker, text));

    private void OnLiveCleared()
        => DispatcherQueue.TryEnqueue(() => Vm.Lines.Clear());

    private void OnRenamed(string oldName, string newName)
        => DispatcherQueue.TryEnqueue(() => { Vm.RenameSpeaker(oldName, newName); BuildConversation(); });

    public OverlayViewModel Vm { get; }

    private void Pin_Click(object sender, RoutedEventArgs e)
        => _presenter.IsAlwaysOnTop = ((ToggleButton)sender).IsChecked == true;

    private static void SuppressWindowFrame(IntPtr hwnd)
    {
        var none = DwmwaColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
    }

    // Extending content into the title bar removes the default drag region,
    // so the header is the drag handle. Recomputed on resize for DPI moves.
    private void UpdateDragRects()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var t = HeaderBar.TransformToVisual(RootSurface);
        var p = t.TransformPoint(new Windows.Foundation.Point(0, 0));
        AppWindow.TitleBar.SetDragRectangles(new[]
        {
            new RectInt32((int)(p.X * _scale), (int)(p.Y * _scale),
                          (int)(HeaderBar.ActualWidth * _scale), (int)(HeaderBar.ActualHeight * _scale)),
        });
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TranscriptLine line)
            return;
        _renaming = line;
        _renameBox.Text = line.Speaker;
        _renameFlyout.ShowAt((FrameworkElement)sender);
        _renameBox.SelectAll();
        _renameBox.Focus(FocusState.Programmatic);
    }

    private void CommitRename()
    {
        if (_renaming is not null)
        {
            // Route to the live session first (syncs Live + storage), then reflect locally.
            LiveBridge.Rename(_renaming.Speaker, _renameBox.Text);
            Vm.RenameSpeaker(_renaming.Speaker, _renameBox.Text);
        }
        _renaming = null;
        _renameFlyout.Hide();
    }

    private Flyout BuildRenameFlyout()
    {
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        cancel.Click += (_, _) => _renameFlyout.Hide();
        var save = new Button { Content = "Save", MinWidth = 88 };
        save.Click += (_, _) => CommitRename();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        var content = new StackPanel { Spacing = 8, Width = 260 };
        content.Children.Add(new TextBlock { Text = "Rename speaker", FontSize = 12 });
        content.Children.Add(_renameBox);
        content.Children.Add(actions);
        _renameBox.PlaceholderText = "e.g. Alice — updates every line";
        _renameBox.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) CommitRename(); };
        return new Flyout { Content = content };
    }

    // Every overlay setting in one place (owner-requested): background darkness,
    // text size, text font, lines shown, keep-on-top. Each control applies live
    // and persists to the same keys the Settings page reads.
    private Flyout BuildSettingsFlyout()
    {
        var darknessValue = new TextBlock { Text = Vm.Opacity.ToString("0.00"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        _backdropSlider.Value = Vm.Opacity;
        _backdropSlider.ValueChanged += (_, e) => { Vm.Opacity = e.NewValue; darknessValue.Text = e.NewValue.ToString("0.00"); Persist("overlay.opacity", e.NewValue.ToString()); };
        AutomationProperties.SetName(_backdropSlider, "Background darkness");

        var textSizeSlider = new Slider { Minimum = 12, Maximum = 48, StepFrequency = 2, Value = Vm.FontSize };
        var textSizeValue = new TextBlock { Text = ((int)Vm.FontSize).ToString(), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        textSizeSlider.ValueChanged += (_, e) => { Vm.FontSize = e.NewValue; textSizeValue.Text = ((int)e.NewValue).ToString(); Persist("overlay.font", e.NewValue.ToString()); };
        AutomationProperties.SetName(textSizeSlider, "Text size");

        var fonts = new[] { "Segoe UI", "Segoe UI Variable", "Consolas", "Georgia", "Verdana", "Arial" };
        var fontBox = new ComboBox { ItemsSource = fonts, HorizontalAlignment = HorizontalAlignment.Stretch };
        fontBox.SelectedItem = fonts.Contains(Vm.FontFamily) ? Vm.FontFamily : fonts[0];
        fontBox.SelectionChanged += (_, _) => { if (fontBox.SelectedItem is string f) { Vm.FontFamily = f; Persist("overlay.fontfamily", f); } };
        AutomationProperties.SetName(fontBox, "Text font");

        var linesSlider = new Slider { Minimum = 1, Maximum = 10, StepFrequency = 1, Value = Vm.ViewLines };
        var linesValue = new TextBlock { Text = ((int)Vm.ViewLines).ToString(), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        linesSlider.ValueChanged += (_, e) => { Vm.ViewLines = e.NewValue; linesValue.Text = ((int)e.NewValue).ToString(); Persist("overlay.viewlines", ((int)e.NewValue).ToString()); };
        AutomationProperties.SetName(linesSlider, "Lines shown");

        var pinSwitch = new ToggleSwitch { IsOn = Vm.IsPinned, OnContent = "On", OffContent = "Off" };
        pinSwitch.Toggled += (_, _) => { Vm.IsPinned = pinSwitch.IsOn; Persist("overlay.pin", pinSwitch.IsOn ? "on" : "off"); };
        AutomationProperties.SetName(pinSwitch, "Keep above other windows");

        var content = new StackPanel { Spacing = 12, Width = 300 };
        content.Children.Add(new TextBlock { Text = "Overlay settings", FontWeight = FontWeights.SemiBold });
        content.Children.Add(Labeled("Background darkness", _backdropSlider, darknessValue));
        content.Children.Add(Labeled("Text size", textSizeSlider, textSizeValue));
        content.Children.Add(Labeled("Text font", fontBox, null));
        content.Children.Add(Labeled("Lines shown", linesSlider, linesValue));
        content.Children.Add(Labeled("Keep above other windows", pinSwitch, null));
        // The strip sits at the screen bottom AND is only 220 DIP tall: a flyout
        // constrained to the window root is clipped to ~2 rows (proven on-screen),
        // so it must be allowed to render outside the window bounds and open upward.
        return new Flyout { Content = content, ShouldConstrainToRootBounds = false, Placement = FlyoutPlacementMode.Top };
    }

    private static StackPanel Labeled(string label, FrameworkElement control, FrameworkElement? trailing)
    {
        var row = new StackPanel { Spacing = 4 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = label, FontSize = 12 });
        if (trailing is not null) header.Children.Add(trailing);
        row.Children.Add(header);
        row.Children.Add(control);
        return row;
    }

    private void Persist(string key, string value)
    {
        try { AppStore.Repo.SetSetting(key, value); } catch { }
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TranscriptList.ItemsSource = Vm.Lines.ToArray();
        RefreshListState();
        BuildConversation();
    }

    private void RefreshListState()
    {
        EmptyState.Visibility = Vm.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (TranscriptList.Items.Count > 0)
            TranscriptList.ScrollIntoView(TranscriptList.Items[TranscriptList.Items.Count - 1]);
    }

    // Bottom-anchored conversation: newest line big and bold (click to rename
    // its speaker), older lines smaller and dimmer above it. Rename rides the
    // button's DataContext through Rename_Click (null-safe early return).
    private void BuildConversation()
    {
        _conversationPanel.Children.Clear();
        if (Vm.Lines.Count == 0)
        {
            _conversationPanel.Children.Add(_noLines);
            return;
        }
        var take = Math.Clamp((int)Vm.ViewLines, 1, 12);
        for (var i = Math.Max(0, Vm.Lines.Count - take); i < Vm.Lines.Count; i++)
        {
            var line = Vm.Lines[i];
            var isLatest = i == Vm.Lines.Count - 1;
            var text = new TextBlock
            {
                Text = CaptionPlacement.FormatCaption(line.Speaker, line.Text),
                TextWrapping = TextWrapping.WrapWholeWords,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White),
                FontFamily = new FontFamily(Vm.FontFamily),
                FontSize = isLatest ? Vm.FontSize + 12 : Vm.FontSize,
                FontWeight = isLatest ? FontWeights.Bold : FontWeights.Normal,
                Opacity = isLatest ? 1.0 : 0.72,
            };
            if (!isLatest) { _conversationPanel.Children.Add(text); continue; }
            var button = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                DataContext = line,
                Content = text,
            };
            button.Click += Rename_Click;
            ToolTipService.SetToolTip(button, "Rename this speaker on every line");
            _conversationPanel.Children.Add(button);
        }
    }
}

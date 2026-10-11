using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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
    // Pre-layout fallback for the conversation cell (row 1): the 220 DIP window
    // minus the header row (20 pt title line ≈ 27 + 8+8 DIP margins = 43) minus
    // the ~8 DIP the window frame keeps from the XAML surface ≈ 169 DIP — the
    // live measurement at 100% DPI. Only the ctor's build runs on it; the Loaded
    // rebuild and every later SizeChanged use the real ActualHeight.
    private const double EstimatedCellHeightDip = 169;

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
    // Meeting language control in the header (code-built like the cogwheel): the same
    // single applier as the Live page combo, through LiveBridge.RequestLanguage.
    private ComboBox? _langCombo;
    private bool _suppressLang;
    // Screen-recording control (F): code-built like the cogwheel; the indicator + button
    // mirror LiveBridge.ScreenStateChanged only (single source of truth).
    private Button? _screenButton;
    private Microsoft.UI.Xaml.Shapes.Ellipse? _screenDot;
    private TextBlock? _screenRecText;
    private StackPanel? _screenIndicator;
    // Conversation view: a bottom-anchored stack of recent lines at one uniform
    // size and weight (brightness marks the newest). Code-built — XamlCompiler 1.8
    // rejects new direct children of the root Grid in XAML; Border AND Rectangle
    // both bisected exit-1.
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
        Text = Loc.T("Ov.NoLines"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.7,
    };
    // The streaming partial line: updated IN PLACE on every partial (no rebuild), so
    // partial churn cannot dismiss an open settings flyout — a final line still rebuilds.
    private TextBlock? _partialBlock;
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
        AutomationProperties.SetName(_settingsButton, Loc.T("Ov.Settings"));
        ToolTipService.SetToolTip(_settingsButton, Loc.T("Ov.Settings"));
        _settingsButton.Flyout = BuildSettingsFlyout();
        Grid.SetColumn(_settingsButton, 4);
        HeaderBar.Children.Add(_settingsButton);
        // Language combo: a meeting control (not an overlay-display setting), so it sits
        // in the header beside the title — the one-settings-entry rule stays intact.
        _langCombo = new ComboBox { Width = 128, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_langCombo, Loc.T("LiveLanguageLabel.Text"));
        Grid.SetColumn(_langCombo, 1);
        _langCombo.Items.Add(Loc.T("Lang.Auto"));
        foreach (var l in MeetVault.Core.EngineCatalog.Languages) _langCombo.Items.Add(l.Endonym);
        _langCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressLang) return;
            var idx = _langCombo.SelectedIndex;
            if (idx < 0) return;
            LiveBridge.RequestLanguage(idx == 0 ? "auto" : MeetVault.Core.EngineCatalog.Languages[idx - 1].Code);
        };
        HeaderBar.Children.Add(_langCombo);
        MeetVault.Core.AppLog.Info("overlay: language combo added (" + _langCombo.Items.Count + " items)");
        LiveBridge.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LiveBridge.LanguageChanged -= OnLanguageChanged;
        _screenDot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Colors.Red), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0), Visibility = Visibility.Collapsed };
        _screenRecText = new TextBlock { Text = Loc.T("Ov.Rec"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Visibility = Visibility.Collapsed };
        _screenIndicator = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _screenIndicator.Children.Add(_screenDot);
        _screenIndicator.Children.Add(_screenRecText);
        Grid.SetColumn(_screenIndicator, 2);
        HeaderBar.Children.Add(_screenIndicator);
        _screenButton = new Button { Content = "⏺", Width = 32, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        ToolTipService.SetToolTip(_screenButton, Loc.T("Ov.ScreenToggle"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_screenButton, Loc.T("Ov.ScreenToggle"));
        _screenButton.Click += (_, _) => LiveBridge.RequestScreenToggle();
        Grid.SetColumn(_screenButton, 3);
        HeaderBar.Children.Add(_screenButton);
        LiveBridge.ScreenStateChanged += OnScreenStateChanged;
        Closed += (_, _) => LiveBridge.ScreenStateChanged -= OnScreenStateChanged;
        LiveBridge.MeetingStateChanged += OnMeetingStateForControls;
        Closed += (_, _) => LiveBridge.MeetingStateChanged -= OnMeetingStateForControls;
        // NOTE: visual-parent lookup is null in the ctor (proven twice by
        // crash-log NRE) — the tree is only attached at Loaded. Host the
        // conversation panel then; text state is already correct via BuildConversation.
        RootSurface.Loaded += (_, _) =>
        {
            if (HeaderBar.Children.Count > 0 && HeaderBar.Children[0] is TextBlock headerTitle) headerTitle.Text = Loc.T("Ov.Title");
            UpdateTitleLanguage(LiveBridge.CurrentLanguage);
            SyncOverlayLanguageCombo(LiveBridge.CurrentLanguage);
            OnScreenStateChanged(LiveBridge.ScreenRecording);
            OnMeetingStateForControls(LiveBridge.State);
            ConversationCell.Children.Add(_conversationPanel);
            // The ctor's build ran on the estimate (no ActualHeight yet): re-fit
            // against the real cell height now, and keep both the fit and the clip
            // honest when the strip is resized or its content reflows.
            ConversationCell.SizeChanged += (_, _) => BuildConversation();
            _conversationPanel.SizeChanged += (_, _) => UpdateConversationClip();
            BuildConversation();
        };
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
        // The header's children change size when the REC indicator toggles: recompute the
        // carved-out drag rects so no dead zone is left behind (review r17).
        HeaderBar.SizeChanged += (_, _) => UpdateDragRects();
        _presenter = (OverlappedPresenter)AppWindow.Presenter;
        _presenter.IsAlwaysOnTop = Vm.IsPinned;
        ((INotifyCollectionChanged)Vm.Lines).CollectionChanged += OnLinesChanged;
        LiveBridge.LineAdded += OnLiveLine;
        LiveBridge.Cleared += OnLiveCleared;
        LiveBridge.LiveChanged += OnLiveChanged;
        LiveBridge.PartialChanged += OnPartial;
        Closed += (_, _) => { LiveBridge.LineAdded -= OnLiveLine; LiveBridge.Cleared -= OnLiveCleared; LiveBridge.LiveChanged -= OnLiveChanged; LiveBridge.PartialChanged -= OnPartial; _acrylic?.Dispose(); };
        // A user dismissal mid-run sticks for the rest of the meeting (review r17): the
        // auto-open must not resurrect the overlay on the next pause→resume.
        Closed += (_, _) => { if (LiveBridge.State != LiveBridge.MeetingState.Idle) UserDismissed = true; };
        foreach (var (speaker, text) in LiveBridge.Recent()) Vm.AddLine(speaker, text); // catch up mid-meeting
        BuildConversation();
    }

    // True once the user closed the overlay during an active meeting; reset at Idle
    // (review r17).
    public static bool UserDismissed;

    // Settings -> Overlay: the controls persist these, so the window must honor them.
    private void SeedFromSettings()
    {
        try
        {
            var repo = AppStore.Repo;
            Vm.IsPinned = repo.GetSetting("overlay.pin") != "off";
            if (double.TryParse(repo.GetSetting("overlay.opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var op)) Vm.Opacity = op;
            if (double.TryParse(repo.GetSetting("overlay.font"), NumberStyles.Float, CultureInfo.InvariantCulture, out var font)) Vm.FontSize = font;
            // One visible-line setting for the whole app: this window's cogwheel flyout
            // slider and the Settings row both read/write overlay.viewlines. overlay.lines
            // is the retired key; an install that saved it keeps its value — read the new
            // key first, fall back to the old one once, and persist the fallback forward
            // so the old row stops being read. (Vm.LineCount, the history-buffer retention,
            // keeps its own default and is unrelated.)
            var viewLines = repo.GetSetting("overlay.viewlines");
            if (string.IsNullOrEmpty(viewLines))
            {
                viewLines = repo.GetSetting("overlay.lines");
                if (!string.IsNullOrEmpty(viewLines)) repo.SetSetting("overlay.viewlines", viewLines);
            }
            if (double.TryParse(viewLines, NumberStyles.Float, CultureInfo.InvariantCulture, out var lines)) Vm.ViewLines = lines;
            var family = repo.GetSetting("overlay.fontfamily");
            if (!string.IsNullOrWhiteSpace(family)) Vm.FontFamily = family;
        }
        catch { }
    }

    private void OnLiveLine(string speaker, string text)
        => DispatcherQueue.TryEnqueue(() => Vm.AddLine(speaker, text));

    private void OnLiveCleared()
        => DispatcherQueue.TryEnqueue(() => { Vm.Lines.Clear(); Vm.Partial = ""; });

    private void OnLiveChanged(bool live)
        => DispatcherQueue.TryEnqueue(() => { Vm.Partial = ""; BuildConversation(); });

    // Partial updates touch only the dedicated block (in place), so an open rename flyout
    // survives; only the first partial after a rebuild (or an unexpected state) rebuilds.
    private void OnPartial(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Vm.Partial = text;
            if (_partialBlock is not null)
            {
                _partialBlock.Text = text;
                _partialBlock.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateConversationClip();
            }
            else BuildConversation();
        });
    }

    public OverlayViewModel Vm { get; }

    private static void SuppressWindowFrame(IntPtr hwnd)
    {
        var none = DwmwaColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
    }

    // Extending content into the title bar removes the default drag region, so the header
    // is the drag handle. Interactive children inside a drag rectangle never receive a
    // real mouse click — the OS hit-tests the rectangle as the caption instead (UIA
    // Invoke bypasses hit-testing, so walkthroughs do not catch it). Every control child
    // is carved OUT of the region; only the gaps (including the title text) drag.
    // Recomputed on resize for DPI moves.
    private void UpdateDragRects()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        try
        {
            var p = HeaderBar.TransformToVisual(RootSurface).TransformPoint(new Windows.Foundation.Point(0, 0));
            var left = p.X * _scale;
            var top = p.Y * _scale;
            var right = left + HeaderBar.ActualWidth * _scale;
            var bottom = top + HeaderBar.ActualHeight * _scale;
            var carve = new List<(double X, double Right)>();
            foreach (var child in HeaderBar.Children)
            {
                if (child is not FrameworkElement { Visibility: Visibility.Visible } fe) continue;
                if (fe is TextBlock || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) continue;
                var q = fe.TransformToVisual(RootSurface).TransformPoint(new Windows.Foundation.Point(0, 0));
                carve.Add((q.X * _scale, q.X * _scale + fe.ActualWidth * _scale));
            }
            var rects = new List<RectInt32>();
            var cursor = left;
            foreach (var (x, r) in carve.OrderBy(c => c.X))
            {
                if (x - cursor >= 1) rects.Add(new RectInt32((int)cursor, (int)top, (int)(x - cursor), (int)(bottom - top)));
                cursor = Math.Max(cursor, r);
            }
            if (right - cursor >= 1) rects.Add(new RectInt32((int)cursor, (int)top, (int)(right - cursor), (int)(bottom - top)));
            if (rects.Count == 0) rects.Add(new RectInt32((int)left, (int)top, (int)(right - left), (int)(bottom - top)));
            AppWindow.TitleBar.SetDragRectangles(rects.ToArray());
        }
        catch (Exception ex) { MeetVault.Core.AppLog.Warn("overlay: drag rects failed: " + ex.Message); }
    }

    private void OnScreenStateChanged(bool active)
        => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var visibility = active ? Visibility.Visible : Visibility.Collapsed;
                _screenDot!.Visibility = visibility;
                _screenRecText!.Visibility = visibility;
            }
            catch (Exception ex) { AppLog.Warn("overlay: screen state update failed: " + ex.Message); }
        });

    // Requests are only consumed while the Live page is loaded and recording; outside that
    // the overlay's meeting controls go inert instead of silently dropping picks (review r5).
    private void OnMeetingStateForControls(LiveBridge.MeetingState state)
        => DispatcherQueue.TryEnqueue(() =>
        {
            var enabled = state == LiveBridge.MeetingState.Recording;
            if (_langCombo is not null) _langCombo.IsEnabled = enabled;
            if (_screenButton is not null) _screenButton.IsEnabled = enabled;
        });

    // Language mirror: the combo and the title suffix follow LiveBridge (the Live page is
    // the owner of the meeting language; the overlay only requests changes).
    private void SyncOverlayLanguageCombo(LiveBridge.LanguageInfo? info)
    {
        if (_langCombo is null) return;
        _suppressLang = true;
        try
        {
            if (info is null) { _langCombo.SelectedIndex = -1; return; }
            _langCombo.SelectedIndex = info.Auto
                ? 0
                : Math.Max(0, Array.FindIndex(MeetVault.Core.EngineCatalog.Languages, l => l.Code == info.Code) + 1);
        }
        finally { _suppressLang = false; }
    }

    private void OnLanguageChanged(LiveBridge.LanguageInfo info)
        => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                SyncOverlayLanguageCombo(info);
                UpdateTitleLanguage(info);
            }
            catch (Exception ex) { AppLog.Warn("overlay: language update failed: " + ex.Message); }
        });

    // "Live transcript · 日本語 (auto)" — the suffix names the ACTIVE language and marks
    // an automatically detected one.
    private void UpdateTitleLanguage(LiveBridge.LanguageInfo? info)
    {
        if (HeaderBar.Children.Count == 0 || HeaderBar.Children[0] is not TextBlock title) return;
        var suffix = info is null
            ? ""
            : " · " + LiveBridge.LanguageDisplay(info.Code) + (info.Auto && info.Detected ? " (" + Loc.T("Ov.LangAuto") + ")" : "");
        title.Text = Loc.T("Ov.Title") + suffix;
    }

    // Every overlay setting in one place (owner-requested): background darkness,
    // text size, text font, lines shown, keep-on-top. Each control applies live
    // and persists to the same keys the Settings page reads.
    private Flyout BuildSettingsFlyout()
    {
        var darknessValue = new TextBlock { Text = Vm.Opacity.ToString("0.00"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        _backdropSlider.Value = Vm.Opacity;
        _backdropSlider.ValueChanged += (_, e) => { Vm.Opacity = e.NewValue; darknessValue.Text = e.NewValue.ToString("0.00"); Persist("overlay.opacity", e.NewValue.ToString(CultureInfo.InvariantCulture)); };
        AutomationProperties.SetName(_backdropSlider, Loc.T("Ov.Darkness"));

        var textSizeSlider = new Slider { Minimum = 12, Maximum = 48, StepFrequency = 2, Value = Vm.FontSize };
        var textSizeValue = new TextBlock { Text = ((int)Vm.FontSize).ToString(), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        textSizeSlider.ValueChanged += (_, e) => { Vm.FontSize = e.NewValue; textSizeValue.Text = ((int)e.NewValue).ToString(); Persist("overlay.font", e.NewValue.ToString(CultureInfo.InvariantCulture)); };
        AutomationProperties.SetName(textSizeSlider, Loc.T("Ov.TextSize"));

        var fonts = new[] { "Segoe UI", "Segoe UI Variable", "Consolas", "Georgia", "Verdana", "Arial" };
        var fontBox = new ComboBox { ItemsSource = fonts, HorizontalAlignment = HorizontalAlignment.Stretch };
        fontBox.SelectedItem = fonts.Contains(Vm.FontFamily) ? Vm.FontFamily : fonts[0];
        fontBox.SelectionChanged += (_, _) => { if (fontBox.SelectedItem is string f) { Vm.FontFamily = f; Persist("overlay.fontfamily", f); } };
        AutomationProperties.SetName(fontBox, Loc.T("Ov.Font"));

        var linesSlider = new Slider { Minimum = 1, Maximum = 10, StepFrequency = 1, Value = Vm.ViewLines };
        var linesValue = new TextBlock { Text = ((int)Vm.ViewLines).ToString(), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        linesSlider.ValueChanged += (_, e) => { Vm.ViewLines = e.NewValue; linesValue.Text = ((int)e.NewValue).ToString(); Persist("overlay.viewlines", ((int)e.NewValue).ToString()); };
        AutomationProperties.SetName(linesSlider, Loc.T("Ov.Lines"));

        var pinSwitch = new ToggleSwitch { IsOn = Vm.IsPinned, OnContent = Loc.T("Ov.On"), OffContent = Loc.T("Ov.Off") };
        pinSwitch.Toggled += (_, _) => { Vm.IsPinned = pinSwitch.IsOn; Persist("overlay.pin", pinSwitch.IsOn ? "on" : "off"); };
        AutomationProperties.SetName(pinSwitch, Loc.T("Ov.Pin"));

        var content = new StackPanel { Spacing = 12, Width = 300 };
        content.Children.Add(new TextBlock { Text = Loc.T("Ov.Settings"), FontWeight = FontWeights.SemiBold });
        content.Children.Add(Labeled(Loc.T("Ov.Darkness"), _backdropSlider, darknessValue));
        content.Children.Add(Labeled(Loc.T("Ov.TextSize"), textSizeSlider, textSizeValue));
        content.Children.Add(Labeled(Loc.T("Ov.Font"), fontBox, null));
        content.Children.Add(Labeled(Loc.T("Ov.Lines"), linesSlider, linesValue));
        content.Children.Add(Labeled(Loc.T("Ov.Pin"), pinSwitch, null));
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
        => BuildConversation();

    // Bottom-anchored conversation: every line — the current sentence included — renders
    // at the same size and weight (owner convention 2026-10-11: no headline treatment);
    // only brightness marks recency (newest 1.0, older 0.72).
    // The strip must never outgrow its cell: WinUI paints a Grid child unclipped,
    // so a bottom-anchored over-tall panel covers the header and its top lines
    // leave the window. The shown count is capped by what actually fits (the fit
    // runs on one size for every line); UpdateConversationClip is the hard
    // guarantee on top of the clamp.
    private void BuildConversation()
    {
        _conversationPanel.Children.Clear();
        _partialBlock = null;
        if (Vm.Lines.Count == 0 && Vm.Partial.Length == 0)
        {
            _noLines.Text = LiveBridge.IsLive
                ? Loc.T("Ov.Listening")
                : Loc.T("Ov.NotRecording");
            _conversationPanel.Children.Add(_noLines);
            UpdateConversationClip();
            return;
        }
        var available = ConversationCellHeight() - _conversationPanel.Margin.Bottom;
        // A shown partial line occupies one regular line box (font x 1.35 + spacing, the
        // same box OverlayLayout.FittingLines counts); reserve it before fitting finals.
        var reserve = Vm.Partial.Length > 0 ? Vm.FontSize * 1.35 + _conversationPanel.Spacing : 0;
        var fitting = OverlayLayout.FittingLines(Math.Max(0, available - reserve), Vm.FontSize, _conversationPanel.Spacing);
        var take = Math.Clamp(Math.Min((int)Vm.ViewLines, fitting), 1, 12);
        for (var i = Math.Max(0, Vm.Lines.Count - take); i < Vm.Lines.Count; i++)
        {
            var line = Vm.Lines[i];
            var isLatest = i == Vm.Lines.Count - 1;
            var text = new TextBlock
            {
                Text = CaptionPlacement.FormatCaption(line.Speaker, line.Text),
                TextWrapping = TextWrapping.WrapWholeWords,
                TextAlignment = TextAlignment.Left,
                Margin = new Thickness(16, 0, 16, 0),
                Foreground = new SolidColorBrush(Colors.White),
                FontFamily = new FontFamily(Vm.FontFamily),
                FontSize = Vm.FontSize,          // the current sentence is a normal line, not a headline
                FontWeight = FontWeights.Normal,
                Opacity = isLatest ? 1.0 : 0.72, // brightness alone marks the newest line
            };
            _conversationPanel.Children.Add(text);
        }
        if (Vm.Partial.Length > 0)
        {
            _partialBlock = new TextBlock
            {
                Text = Vm.Partial,
                TextWrapping = TextWrapping.WrapWholeWords,
                TextAlignment = TextAlignment.Left,
                Margin = new Thickness(16, 0, 16, 0),
                Foreground = new SolidColorBrush(Colors.White),
                FontFamily = new FontFamily(Vm.FontFamily),
                FontSize = Vm.FontSize,
                Opacity = 0.72,
            };
            _conversationPanel.Children.Add(_partialBlock);
        }
        UpdateConversationClip();
    }

    // The row-1 cell that hosts the conversation panel: measurable once the panel
    // is parented at Loaded; before that there is no ActualHeight, so the fit runs
    // on the computed estimate (the ctor's build is invisible pre-layout, and the
    // Loaded rebuild re-fits against the real height).
    private double ConversationCellHeight()
    {
        var cell = _conversationPanel.Parent as FrameworkElement;
        return cell is { ActualHeight: > 0 } ? cell.ActualHeight : EstimatedCellHeightDip;
    }

    // Belt-and-braces: the clamp fits whole lines, but a wrapped line can still
    // make the panel taller than its cell. UIElement.Clip is in the panel's own
    // coordinates, where the cell's top edge sits at (height + bottom margin −
    // cellHeight) from the panel's top — everything above that is cut, so nothing
    // can overpaint the header even mid-layout. Updated on every rebuild and on
    // the panel's size changes (which is when a reflow actually lands).
    private void UpdateConversationClip()
    {
        var h = _conversationPanel.ActualHeight;
        var w = _conversationPanel.ActualWidth;
        var cell = _conversationPanel.Parent as FrameworkElement;
        if (cell is not { ActualHeight: > 0 } || h <= 0 || w <= 0)
        {
            _conversationPanel.Clip = null;
            return;
        }
        var overflow = h + _conversationPanel.Margin.Bottom - cell.ActualHeight;
        var cut = Math.Clamp(overflow, 0, h); // never a negative-height rect (review r15)
        _conversationPanel.Clip = cut <= 0
            ? null
            : new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, cut, w, h - cut) };
    }
}

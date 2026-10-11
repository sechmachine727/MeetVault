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
        // Same ABI rule as Live/Details: ObservableCollection NREs in ComWrappers
        // vtable computation in the trimmed exe. Array snapshot, refreshed on change.
        TranscriptList.ItemsSource = Vm.Lines.ToArray();
        // Conversation mode replaces the history list. History buffer stays intact.
        AutomationProperties.SetName(_settingsButton, Loc.T("Ov.Settings"));
        ToolTipService.SetToolTip(_settingsButton, Loc.T("Ov.Settings"));
        _settingsButton.Flyout = BuildSettingsFlyout();
        Grid.SetColumn(_settingsButton, 2);
        HeaderBar.Children.Add(_settingsButton);
        // NOTE: visual-parent lookup is null in the ctor (proven twice by
        // crash-log NRE) — the tree is only attached at Loaded. Host the
        // conversation panel then; text state is already correct via BuildConversation.
        RootSurface.Loaded += (_, _) =>
        {
            if (HeaderBar.Children.Count > 0 && HeaderBar.Children[0] is TextBlock headerTitle) headerTitle.Text = Loc.T("Ov.Title");
            TranscriptList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Collapsed;
            var conversationCell = (Grid)VisualTreeHelper.GetParent(TranscriptList);
            conversationCell.Children.Add(_conversationPanel);
            // The ctor's build ran on the estimate (no ActualHeight yet): re-fit
            // against the real cell height now, and keep both the fit and the clip
            // honest when the strip is resized or its content reflows.
            conversationCell.SizeChanged += (_, _) => BuildConversation();
            _conversationPanel.SizeChanged += (_, _) => UpdateConversationClip();
            BuildConversation();
            // Consolidation: the old scattered controls go away — Pin + Backdrop
            // in the header, Font size + Lines in the footer all live in the
            // cogwheel flyout now.
            if (HeaderBar.Children.Count > 1 && HeaderBar.Children[1] is FrameworkElement pinToggle) pinToggle.Visibility = Visibility.Collapsed;
            BackdropButton.Visibility = Visibility.Collapsed;
            foreach (var child in RootSurface.Children)
                if (child is Grid footer && Grid.GetRow(footer) == 2) footer.Visibility = Visibility.Collapsed;
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
        _presenter = (OverlappedPresenter)AppWindow.Presenter;
        _presenter.IsAlwaysOnTop = Vm.IsPinned;
        ((INotifyCollectionChanged)Vm.Lines).CollectionChanged += OnLinesChanged;
        LiveBridge.LineAdded += OnLiveLine;
        LiveBridge.Cleared += OnLiveCleared;
        LiveBridge.LiveChanged += OnLiveChanged;
        LiveBridge.PartialChanged += OnPartial;
        Closed += (_, _) => { LiveBridge.LineAdded -= OnLiveLine; LiveBridge.Cleared -= OnLiveCleared; LiveBridge.LiveChanged -= OnLiveChanged; LiveBridge.PartialChanged -= OnPartial; _acrylic?.Dispose(); };
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
            if (double.TryParse(viewLines, out var lines)) Vm.ViewLines = lines;
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

    // Every overlay setting in one place (owner-requested): background darkness,
    // text size, text font, lines shown, keep-on-top. Each control applies live
    // and persists to the same keys the Settings page reads.
    private Flyout BuildSettingsFlyout()
    {
        var darknessValue = new TextBlock { Text = Vm.Opacity.ToString("0.00"), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        _backdropSlider.Value = Vm.Opacity;
        _backdropSlider.ValueChanged += (_, e) => { Vm.Opacity = e.NewValue; darknessValue.Text = e.NewValue.ToString("0.00"); Persist("overlay.opacity", e.NewValue.ToString()); };
        AutomationProperties.SetName(_backdropSlider, Loc.T("Ov.Darkness"));

        var textSizeSlider = new Slider { Minimum = 12, Maximum = 48, StepFrequency = 2, Value = Vm.FontSize };
        var textSizeValue = new TextBlock { Text = ((int)Vm.FontSize).ToString(), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        textSizeSlider.ValueChanged += (_, e) => { Vm.FontSize = e.NewValue; textSizeValue.Text = ((int)e.NewValue).ToString(); Persist("overlay.font", e.NewValue.ToString()); };
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
    {
        TranscriptList.ItemsSource = Vm.Lines.ToArray();
        RefreshListState();
        BuildConversation();
    }

    // The conversation panel owns the empty text (same Grid cell as the old XAML
    // EmptyState, collapsed once at load); re-showing EmptyState here would stack
    // two empty messages on every Clear.
    private void RefreshListState()
    {
        if (TranscriptList.Items.Count > 0)
            TranscriptList.ScrollIntoView(TranscriptList.Items[TranscriptList.Items.Count - 1]);
    }

    // Bottom-anchored conversation: every line — the current sentence included — renders
    // at the same size and weight (owner convention 2026-10-11: no headline treatment);
    // only brightness marks recency (newest 1.0, older 0.72). Rename rides the button's
    // DataContext through Rename_Click (null-safe early return).
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
        var fitting = OverlayLayout.FittingLines(Math.Max(0, available - reserve), Vm.FontSize, Vm.FontSize, _conversationPanel.Spacing);
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
        _conversationPanel.Clip = overflow <= 0
            ? null
            : new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, overflow, w, h - overflow) };
    }
}

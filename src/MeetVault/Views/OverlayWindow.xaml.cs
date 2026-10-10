using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using MeetVault.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace MeetVault.Views;

public sealed partial class OverlayWindow : Window
{
    private const double WidthDip = 1100;
    private const double HeightDip = 220;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    private readonly OverlappedPresenter _presenter;
    private readonly double _scale;
    // Veil is code-built, not XAML: this file's XamlCompiler rejects new direct
    // children of the root Grid (bisected: Border and Rectangle both exit 1).
    // The Backdrop slider is code-built too: x:Bind TwoWay inside Button.Flyout
    // never pushed values back to Vm.Opacity (proven: identical pixels at 1.0/0.3).
    private readonly SolidColorBrush _veil = new(Colors.Black);
    private readonly Slider _backdropSlider = new() { Minimum = 0.3, Maximum = 1, StepFrequency = 0.05 };
    // YouTube-style caption: one transparent button wrapping a centered text
    // block (code-built — XamlCompiler 1.8 rejects new direct children of the
    // root Grid in XAML; Border AND Rectangle both bisected exit-1).
    private readonly Button _captionButton = new()
    {
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _captionBody = new()
    {
        TextWrapping = TextWrapping.WrapWholeWords,
        MaxLines = 2,
        TextAlignment = TextAlignment.Center,
        Foreground = new SolidColorBrush(Colors.White),
        FontWeight = FontWeights.Bold,
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
        // True transparency via color key: the magenta base is keyed out at HWND
        // level (WS_EX_LAYERED + LWA_COLORKEY), the black veil above it carries
        // the user darkness, content rows paint above both. Clicks still land
        // (no WS_EX_TRANSPARENT), so Close/rename keep working.
        RootSurface.Background = new SolidColorBrush(Colors.Magenta);
        var veilHost = new Grid { Background = _veil };
        Grid.SetRowSpan(veilHost, 3);
        RootSurface.Children.Insert(0, veilHost);
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.Opacity)) _veil.Opacity = Vm.Opacity;
            if (e.PropertyName == nameof(OverlayViewModel.FontSize)) _captionBody.FontSize = Vm.FontSize + 12;
        };
        // Same ABI rule as Live/Details: ObservableCollection NREs in ComWrappers
        // vtable computation in the trimmed exe. Array snapshot, refreshed on change.
        TranscriptList.ItemsSource = Vm.Lines.ToArray();
        // Caption mode replaces the history list: the row-1 grid hosts the
        // caption button instead. History buffer (Vm.Lines) stays intact.
        _captionButton.Content = _captionBody;
        _captionButton.Click += Rename_Click;
        ToolTipService.SetToolTip(_captionButton, "Rename this speaker on every line");
        TranscriptList.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        ((Grid)TranscriptList.Parent).Children.Add(_captionButton);
        _renameFlyout = BuildRenameFlyout();
        BackdropButton.Flyout = BuildBackdropFlyout();
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        _scale = scale;
        MakeLayeredMagentaTransparent(hwnd);
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
        RootSurface.Loaded += (_, _) => UpdateDragRects();
        RootSurface.SizeChanged += (_, _) => UpdateDragRects();
        _presenter = (OverlappedPresenter)AppWindow.Presenter;
        _presenter.IsAlwaysOnTop = Vm.IsPinned;
        ((INotifyCollectionChanged)Vm.Lines).CollectionChanged += OnLinesChanged;
        LiveBridge.LineAdded += OnLiveLine;
        LiveBridge.Cleared += OnLiveCleared;
        LiveBridge.Renamed += OnRenamed;
        Closed += (_, _) => { LiveBridge.LineAdded -= OnLiveLine; LiveBridge.Cleared -= OnLiveCleared; LiveBridge.Renamed -= OnRenamed; };
        foreach (var (speaker, text) in LiveBridge.Recent()) Vm.AddLine(speaker, text); // catch up mid-meeting
        RefreshListState();
        RefreshCaption();
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
        }
        catch { }
    }

    private void OnLiveLine(string speaker, string text)
        => DispatcherQueue.TryEnqueue(() => Vm.AddLine(speaker, text));

    private void OnLiveCleared()
        => DispatcherQueue.TryEnqueue(() => Vm.Lines.Clear());

    private void OnRenamed(string oldName, string newName)
        => DispatcherQueue.TryEnqueue(() => { Vm.RenameSpeaker(oldName, newName); RefreshCaption(); });

    public OverlayViewModel Vm { get; }

    private static void MakeLayeredMagentaTransparent(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const uint WS_EX_LAYERED = 0x00080000;
        const uint LWA_COLORKEY = 0x00000001;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | (int)WS_EX_LAYERED);
        // COLORREF magenta must match Colors.Magenta exactly: RGB(255,0,255).
        SetLayeredWindowAttributes(hwnd, 0x00FF00FF, 0, LWA_COLORKEY);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
        => _presenter.IsAlwaysOnTop = ((ToggleButton)sender).IsChecked == true;

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

    private Flyout BuildBackdropFlyout()
    {
        _backdropSlider.Value = Vm.Opacity;
        _backdropSlider.ValueChanged += (_, e) => Vm.Opacity = e.NewValue;
        var content = new StackPanel { Spacing = 8, Width = 220 };
        content.Children.Add(new TextBlock { Text = "Background darkness", FontSize = 12 });
        content.Children.Add(_backdropSlider);
        return new Flyout { Content = content };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TranscriptList.ItemsSource = Vm.Lines.ToArray();
        RefreshListState();
        RefreshCaption();
    }

    private void RefreshListState()
    {
        EmptyState.Visibility = Vm.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (TranscriptList.Items.Count > 0)
            TranscriptList.ScrollIntoView(TranscriptList.Items[TranscriptList.Items.Count - 1]);
    }

    // Latest line big and centered; speaker rename rides the button's
    // DataContext through the unchanged Rename_Click handler (null-safe:
    // Rename_Click returns early when DataContext is not a TranscriptLine).
    private void RefreshCaption()
    {
        var last = Vm.Lines.Count == 0 ? null : Vm.Lines[Vm.Lines.Count - 1];
        _captionButton.DataContext = last;
        _captionBody.Text = last is null
            ? "No transcript lines yet."
            : CaptionPlacement.FormatCaption(last.Speaker, last.Text);
        _captionBody.FontSize = Vm.FontSize + 12;
    }
}

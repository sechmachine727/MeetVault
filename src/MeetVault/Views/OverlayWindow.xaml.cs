using System;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;
using Windows.System;

namespace MeetVault.Views;

public sealed partial class OverlayWindow : Window
{
    private const double WidthDip = 480;
    private const double HeightDip = 600;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private readonly OverlappedPresenter _presenter;
    private readonly TextBox _renameBox = new();
    private readonly Flyout _renameFlyout;
    private TranscriptLine? _renaming;

    public OverlayWindow()
    {
        Vm = new OverlayViewModel();
        InitializeComponent();
        _renameFlyout = BuildRenameFlyout();
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale)));
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        _presenter = (OverlappedPresenter)AppWindow.Presenter;
        _presenter.IsAlwaysOnTop = Vm.IsPinned;
        ((INotifyCollectionChanged)Vm.Lines).CollectionChanged += OnLinesChanged;
        RefreshListState();
    }

    public OverlayViewModel Vm { get; }

    private void Pin_Click(object sender, RoutedEventArgs e)
        => _presenter.IsAlwaysOnTop = ((ToggleButton)sender).IsChecked == true;

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TranscriptLine line)
            return;
        _renaming = line;
        _renameBox.Text = line.Speaker;
        _renameFlyout.ShowAt((FrameworkElement)sender);
    }

    private void CommitRename()
    {
        if (_renaming is not null)
            Vm.RenameSpeaker(_renaming.Speaker, _renameBox.Text);
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
        var content = new StackPanel { Spacing = 8, Width = 240 };
        content.Children.Add(new TextBlock { Text = "Speaker name", FontSize = 12 });
        content.Children.Add(_renameBox);
        content.Children.Add(actions);
        _renameBox.PlaceholderText = "Name shown on every line";
        _renameBox.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) CommitRename(); };
        return new Flyout { Content = content };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshListState();

    private void RefreshListState()
    {
        EmptyState.Visibility = Vm.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (TranscriptList.Items.Count > 0)
            TranscriptList.ScrollIntoView(TranscriptList.Items[TranscriptList.Items.Count - 1]);
    }
}


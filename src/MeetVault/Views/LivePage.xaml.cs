using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class LivePage : Page
{
    public ObservableCollection<TranscriptLine> Lines { get; } = new();
    public ObservableCollection<string> Speakers { get; } = new();

    public LivePage()
    {
        InitializeComponent();
        TranscriptList.ItemsSource = Lines;
        SpeakerList.ItemsSource = Speakers;
    }

    private void Pause_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { StatusBar.Message = "Paused: capture halted with a gap. Engine wiring lands with P4."; StatusBar.IsOpen = true; }
    private void Overlay_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { OverlayWindow.ShowOrActivate(); }
    private void Stop_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { StatusBar.Message = "Stopped: meeting finalized to local storage. Engine wiring lands with P4."; StatusBar.IsOpen = true; }
    private void Speaker_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { StatusBar.Message = "Rename: click-to-rename syncs both ways once the session binds (P4)."; StatusBar.IsOpen = true; }
}


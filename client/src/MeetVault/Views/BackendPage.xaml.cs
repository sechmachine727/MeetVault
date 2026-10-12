using System.Collections.ObjectModel;
using MeetVault.Core;
using MeetVault.Core.Backend;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MeetVault.Views;

public sealed partial class BackendPage : Page
{
    public BackendPage()
    {
        InitializeComponent();
    }

    // Stubs until their tasks: T71 implements Meeting_SelectionChanged, Refresh_Click, Play_Click,
    // CopyText_Click, TranscriptRow_Tapped, CitationRow_Tapped; T73 the Ask flow; T74 the speaker rows;
    // T75 rerun/update/remove/versions/jobs/media.
    private void Meeting_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void Refresh_Click(object sender, RoutedEventArgs e) { }
    private void Play_Click(object sender, RoutedEventArgs e) { }
    private void Rerun_Click(object sender, RoutedEventArgs e) { }
    private void UpdateTranscript_Click(object sender, RoutedEventArgs e) { }
    private void Remove_Click(object sender, RoutedEventArgs e) { }
    private void CopyText_Click(object sender, RoutedEventArgs e) { }
    private void Ask_Click(object sender, RoutedEventArgs e) { }
    private void SpeakerSave_Click(object sender, RoutedEventArgs e) { }
    private void SpeakerMergeCombo_Loaded(object sender, RoutedEventArgs e) { }
    private void SpeakerMergeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void SpeakerMerge_Click(object sender, RoutedEventArgs e) { }
    private void SpeakerDelete_Click(object sender, RoutedEventArgs e) { }
    private void VersionActivate_Click(object sender, RoutedEventArgs e) { }
    private void VersionDelete_Click(object sender, RoutedEventArgs e) { }
    private void JobWatch_Click(object sender, RoutedEventArgs e) { }
    private void JobCancel_Click(object sender, RoutedEventArgs e) { }
    private void MediaDownload_Click(object sender, RoutedEventArgs e) { }
    private void TranscriptRow_Tapped(object sender, TappedRoutedEventArgs e) { }
    private void CitationRow_Tapped(object sender, TappedRoutedEventArgs e) { }
}

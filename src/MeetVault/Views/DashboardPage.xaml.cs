using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace MeetVault.Views;

public sealed partial class DashboardPage : Page
{
    private MeetVault.Core.Meeting? _recoverable;

    public DashboardPage()
    {
        InitializeComponent();
        var cards = AppStore.Cards();
        RecentMeetings.ItemsSource = cards;
        EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var unfinished = AppStore.Repo.LoadUnfinished();
        if (unfinished.Count > 0)
        {
            _recoverable = unfinished[0];
            RecoveryBar.Message = $"'{_recoverable.Title}' was interrupted. Resume it in Live to keep recording, or start a new meeting; this one stays as a draft.";
            RecoveryBar.IsOpen = true;
        }
        var hour = DateTime.Now.Hour;
        GreetingTitle.Text = hour < 12 ? "Good morning — ready to capture" : hour < 18 ? "Good afternoon — ready to capture" : "Good evening — ready to capture";
        // audio.mic stores the numeric device id; resolve to the live device name.
        var micIdx = AppStore.Repo.GetSetting("audio.mic") ?? "";
        var mic = MeetVault.Core.NAudioCapture.ListDevices().FirstOrDefault(d => d.Id.ToString() == micIdx).Name ?? "Windows default";
        MicStatus.Text = string.IsNullOrWhiteSpace(mic) ? "Not started" : mic;
        var model = AppStore.Repo.GetSetting("transcription.model") == "base" ? "base" : "tiny";
        EngineStatus.Text = model == "base" ? "base (refinement)" : "tiny (live)";
        ModelStatus.Text = EngineModels.Available
            ? $"Multilingual {model} + diarizer ready. Live transcription runs on-device."
            : "Speech model not downloaded. Live runs in demo mode until models arrive.";
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is MeetVault.MainWindow main)
            main.NavigateToLive();
        else
        {
            StatusBar.Message = "Cannot open Live from here. Use the Live item in the navigation pane.";
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.IsOpen = true;
        }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_recoverable is null) return;
        PendingRecovery.MeetingId = _recoverable.Id;
        RecoveryBar.IsOpen = false;
        Start_Click(sender, e);
    }

    // Click a recent meeting: an unfinished draft resumes in Live, a finished meeting opens in Details.
    private void OpenMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MeetingCard card })
        {
            if (card.IsDraft)
            {
                PendingRecovery.MeetingId = card.Id;
                (App.Window as MainWindow)?.NavigateToLive();
            }
            else
            {
                PendingDetails.MeetingId = card.Id;
                (App.Window as MainWindow)?.NavigateToDetails();
            }
        }
    }
}

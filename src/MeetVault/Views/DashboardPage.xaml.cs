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
            RecoveryBar.Message = $"'{_recoverable.Title}' ({_recoverable.Status}) was interrupted. Resume it in Live or leave it; starting a new meeting keeps it as a draft.";
            RecoveryBar.IsOpen = true;
        }
        ModelStatus.Text = EngineModels.Available
            ? "Speech model: multilingual base + diarizer ready."
            : "Speech model: not downloaded. Live runs in demo mode until models arrive.";
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
}


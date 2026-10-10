using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using MeetVault.Core;

namespace MeetVault.Views;

public sealed partial class DashboardPage : Page
{
    private readonly MeetingSelection _selection = new();
    private bool _busy;
    private MeetVault.Core.Meeting? _recoverable;

    public DashboardPage()
    {
        InitializeComponent();
        RefreshList();
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

    private MeetingCard[] VisibleRows => RecentMeetings.ItemsSource as MeetingCard[] ?? Array.Empty<MeetingCard>();

    // --- Batch delete (Edit -> Delete meetings -> select -> Delete selected) ---

    private void EnterSelection_Click(object sender, RoutedEventArgs e)
    {
        _selection.Begin();
        SelectionBar.Visibility = Visibility.Visible;
        HeaderSelectSpacer.Visibility = Visibility.Visible;
        RefreshList();
        StatusBar.Message = "Select the meetings to delete, then choose Delete selected.";
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.IsOpen = true;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _selection.SelectAll(VisibleRows.Where(c => c.IsSelectable).Select(c => c.Id));
        RefreshList();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _selection.Clear();
        RefreshList();
    }

    private void CancelSelection_Click(object sender, RoutedEventArgs e) => ExitSelection();

    private void ExitSelection()
    {
        _selection.End();
        SelectionBar.Visibility = Visibility.Collapsed;
        HeaderSelectSpacer.Visibility = Visibility.Collapsed;
        StatusBar.IsOpen = false;
        RefreshList();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selection.Count == 0) return;
        var ids = _selection.Ids.ToArray();
        var confirm = new ContentDialog
        {
            Title = "Delete meetings?",
            Content = $"Delete {ids.Length} meeting(s)? Their recordings and transcripts will be removed from this device.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        BulkDeleteResult result;
        try { result = AppStore.Repo.DeleteMeetings(ids); }
        catch (Exception ex)
        {
            MeetVault.Core.AppLog.Error("delete: batch delete failed", ex);
            SetBusy(false);
            StatusBar.Message = "Could not delete the selected meetings.";
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
            return;
        }
        SetBusy(false);

        // Failed items stay selected so the user can retry; deleted ones are gone.
        _selection.Clear();
        foreach (var id in result.FailedIds.Keys) _selection.Select(id);

        if (result.FailedIds.Count == 0)
        {
            ExitSelection();
            StatusBar.Message = $"Deleted {result.DeletedIds.Count} meeting(s).";
            StatusBar.Severity = InfoBarSeverity.Success;
        }
        else
        {
            RefreshList();
            StatusBar.Message = $"{result.DeletedIds.Count} deleted, {result.FailedIds.Count} failed - the remaining meetings could not be deleted.";
            StatusBar.Severity = InfoBarSeverity.Warning;
        }
        StatusBar.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RecentMeetings.IsEnabled = !busy;
        SelectAllButton.IsEnabled = !busy;
        ClearSelectionButton.IsEnabled = !busy;
        CancelSelectionButton.IsEnabled = !busy;
        DeleteSelectedButton.Content = busy ? "Deleting..." : "Delete selected";
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        SelectionCount.Text = $"{_selection.Count} selected";
        DeleteSelectedButton.IsEnabled = !_busy && _selection.Count > 0;
    }

    // Click a recent meeting: in selection mode toggle its checkbox (protected
    // rows explain why they cannot be selected); an unfinished draft resumes in
    // Live, a finished meeting opens in Details.
    private void OpenMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MeetingCard card }) return;
        if (_selection.IsActive)
        {
            if (!card.IsSelectable)
            {
                StatusBar.Message = card.SelectReason;
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.IsOpen = true;
                return;
            }
            card.IsSelected = _selection.Toggle(card.Id);
            UpdateToolbar();
            return;
        }
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

    private void RefreshList()
    {
        var cards = AppStore.Cards();
        foreach (var c in cards)
        {
            c.ShowSelect = _selection.IsActive;
            c.IsSelected = _selection.IsActive && _selection.Contains(c.Id);
        }
        RecentMeetings.ItemsSource = cards;
        EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolbar();
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

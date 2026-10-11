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
            RecoveryBar.Message = Loc.Tf("Dash.Interrupted", _recoverable.Title);
            RecoveryBar.IsOpen = true;
        }
        var hour = DateTime.Now.Hour;
        GreetingTitle.Text = hour < 12 ? Loc.T("Dash.GreetingMorning") : hour < 18 ? Loc.T("Dash.GreetingAfternoon") : Loc.T("Dash.GreetingEvening");
        // audio.mic stores the numeric device id; resolve to the live device name.
        var micIdx = AppStore.Repo.GetSetting("audio.mic") ?? "";
        var mic = MeetVault.Core.NAudioCapture.ListDevices().FirstOrDefault(d => d.Id.ToString() == micIdx).Name ?? Loc.T("Dash.WindowsDefault");
        MicStatus.Text = string.IsNullOrWhiteSpace(mic) ? Loc.T("Dash.MicNotStarted") : mic;
        // Same resolution Live runs (EngineModels.ResolveLive): the dashboard must show
        // the effective model, not the configured one, or the two pages disagree.
        var resolution = EngineModels.ResolveLive(AppStore.Repo);
        // Intentionally English (scope cut): an engine/diagnostic identifier, not UI copy — a future localization pass should leave it as is.
        EngineStatus.Text = resolution.Option.Label;
        ModelStatus.Text = resolution.Demo
            ? Loc.T("Dash.ModelMissing")
            : resolution.IsFallback
                ? Loc.Tf("Dash.ModelFallback", resolution.Option.Label)
                : Loc.Tf("Dash.ModelReady", resolution.Option.Label);
    }

    private MeetingCard[] VisibleRows => RecentMeetings.ItemsSource as MeetingCard[] ?? Array.Empty<MeetingCard>();

    // --- Batch delete (Edit -> Delete meetings -> select -> Delete selected) ---

    private void EnterSelection_Click(object sender, RoutedEventArgs e)
    {
        _selection.Begin();
        SelectionBar.Visibility = Visibility.Visible;
        HeaderSelectSpacer.Visibility = Visibility.Visible;
        RefreshList();
        StatusBar.Message = Loc.T("Mtg.SelectHint");
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
            Title = Loc.T("Mtg.DelTitle"),
            Content = Loc.Tf("Mtg.DelBody", ids.Length),
            PrimaryButtonText = Loc.T("Mtg.DelConfirm"),
            CloseButtonText = Loc.T("Mtg.Cancel"),
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
            StatusBar.Message = Loc.T("Mtg.CouldNotDeleteSelected");
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
            if (result.SkippedMedia.Count > 0)
            {
                // Same rule as MeetingsPage: rows deleted but unmanaged recordings left
                // in place must not read as a clean success.
                StatusBar.Message = Loc.Tf("Mtg.MediaSkipped", result.SkippedMedia.Count, string.Join("; ", result.SkippedMedia.Select(s => s.Path)));
                StatusBar.Severity = InfoBarSeverity.Warning;
            }
            else
            {
                StatusBar.Message = Loc.Tf("Mtg.Deleted", result.DeletedIds.Count);
                StatusBar.Severity = InfoBarSeverity.Success;
            }
        }
        else
        {
            RefreshList();
            StatusBar.Message = Loc.Tf("Mtg.Partial", result.DeletedIds.Count, result.FailedIds.Count);
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
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        SelectionCount.Text = Loc.Tf("Mtg.SelectedCount", _selection.Count);
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

    // Content column: fill up to a 1100 DIP reading width and stay centered in the page.
    // (Stretch + MaxWidth does not center reliably in WinUI 3; an explicit clamped width is deterministic.)
    private void RootScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        => ContentColumn.Width = Math.Min(1100, e.NewSize.Width);

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
            StatusBar.Message = Loc.T("Dash.CannotOpenLive");
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

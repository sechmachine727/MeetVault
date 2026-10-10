using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeetVault.Core;

namespace MeetVault.Views;

/// <summary>Display projection of a <see cref="Meeting"/>; keeps formatted text out of the XAML.</summary>
public sealed class MeetingCard : INotifyPropertyChanged
{
    public MeetingCard(string id, string title, string when, string duration, string status, bool isDraft)
    {
        Id = id; Title = title; When = when; Duration = duration; Status = status; IsDraft = isDraft;
    }

    public string Id { get; }
    public string Title { get; }
    public string When { get; }
    public string Duration { get; }
    public string Status { get; }
    public bool IsDraft { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Still-recording or resumable-draft meetings cannot be bulk-deleted from
    // these lists; the Core repository refuses them as well (one rule, both layers).
    public bool IsSelectable => !IsDraft;
    public string SelectReason => IsDraft ? "Still recording or a resumable draft - finish or discard it in Live first." : "";

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Raise(nameof(IsSelected));
            Raise(nameof(RowBackground));
        }
    }

    private bool _showSelect;
    public bool ShowSelect
    {
        get => _showSelect;
        set
        {
            if (_showSelect == value) return;
            _showSelect = value;
            Raise(nameof(CheckVisibility));
            Raise(nameof(DeleteVisibility));
            Raise(nameof(RowTooltip));
        }
    }

    public Visibility CheckVisibility => _showSelect ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteVisibility => _showSelect ? Visibility.Collapsed : Visibility.Visible;

    public string RowTooltip => _showSelect
        ? (IsSelectable ? "Click to select or deselect this meeting" : SelectReason)
        : "Select a meeting to open its details";

    private static readonly Brush ClearBrush = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush SelectedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x00, 0x78, 0xD4));
    public Brush RowBackground => _selected ? SelectedBrush : ClearBrush;
}

public static class MeetingCards
{
    public static MeetingCard ToCard(Meeting m) => new(m.Id, m.Title, m.StartedAt.ToString("g"), Duration(m), StatusLabel(m.Status), m.Status is MeetingStatus.Recording or MeetingStatus.Paused);

    // Plain label for the raw enum.
    public static string StatusLabel(MeetingStatus status) => status switch
    {
        MeetingStatus.Recording => "Recording",
        MeetingStatus.Paused => "Paused",
        MeetingStatus.Processing => "Processing",
        MeetingStatus.Ready => "Ready",
        MeetingStatus.Failed => "Did not finish",
        _ => "Unknown",
    };

    private static string Duration(Meeting m)
    {
        if (m.EndedAt is not { } ended) return "In progress";
        var minutes = (int)Math.Round((ended - m.StartedAt).TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }
}

// Single shared SQLite repo; Meetings/Dashboard project Repo.ListMeetings() to cards.
public static class AppStore
{
    private static LocalMeetingRepository? _repo;

    // M7: single shared secret store; Settings/Details/uploader all use this, never new().
    private static CredentialLockerSecretStore? _secrets;
    public static CredentialLockerSecretStore Secrets => _secrets ??= new();

    public static LocalMeetingRepository Repo
    {
        get
        {
            if (_repo is null)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetVault");
                Directory.CreateDirectory(dir);
                _repo = new LocalMeetingRepository(Path.Combine(dir, "meetings.db"));
            }
            return _repo;
        }
    }

    public static IReadOnlyList<MeetingCard> Cards()
        => Repo.ListMeetings().Select(MeetingCards.ToCard).ToArray();
}

public sealed partial class MeetingsPage : Page
{
    private readonly MeetingSelection _selection = new();
    private bool _busy;

    public MeetingsPage()
    {
        InitializeComponent();
        ApplyFilter(string.Empty);
        StatusBar.Message = $"{AppStore.Cards().Count} meetings from the local repository.";
        StatusBar.IsOpen = true;
    }

    private MeetingCard[] VisibleRows => MeetingList.ItemsSource as MeetingCard[] ?? Array.Empty<MeetingCard>();

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => ApplyFilter(sender.Text);

    // --- Batch delete (Edit -> Delete meetings -> select -> Delete selected) ---

    private void EnterSelection_Click(object sender, RoutedEventArgs e)
    {
        _selection.Begin();
        SelectionBar.Visibility = Visibility.Visible;
        HeaderSelectSpacer.Visibility = Visibility.Visible;
        ApplyFilter(SearchBox.Text);
        StatusBar.Message = "Select the meetings to delete, then choose Delete selected.";
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.IsOpen = true;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _selection.SelectAll(VisibleRows.Where(c => c.IsSelectable).Select(c => c.Id));
        ApplyFilter(SearchBox.Text);
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _selection.Clear();
        ApplyFilter(SearchBox.Text);
    }

    private void CancelSelection_Click(object sender, RoutedEventArgs e) => ExitSelection();

    private void ExitSelection()
    {
        _selection.End();
        SelectionBar.Visibility = Visibility.Collapsed;
        HeaderSelectSpacer.Visibility = Visibility.Collapsed;
        StatusBar.IsOpen = false;
        ApplyFilter(SearchBox.Text);
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
            ApplyFilter(SearchBox.Text);
            StatusBar.Message = $"{result.DeletedIds.Count} deleted, {result.FailedIds.Count} failed - the remaining meetings could not be deleted.";
            StatusBar.Severity = InfoBarSeverity.Warning;
        }
        StatusBar.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        MeetingList.IsEnabled = !busy;
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

    // Click a row: in selection mode toggle its checkbox (protected rows explain
    // why they cannot be selected); otherwise an unfinished draft resumes in Live
    // and a finished meeting opens in Details.
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

    // Delete a meeting (and its recording) after an explicit confirmation.
    private async void DeleteMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MeetingCard card }) return;
        var confirm = new ContentDialog
        {
            Title = "Delete this meeting?",
            Content = $"'{card.Title}' and its transcript will be removed. The recording file is deleted too. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var media = AppStore.Repo.DeleteMeeting(card.Id);
            if (media is { Length: > 0 })
            {
                try { if (File.Exists(media)) File.Delete(media); }
                catch (Exception ex) { MeetVault.Core.AppLog.Warn("delete: could not remove media " + media + " :: " + ex.Message); }
            }
            ApplyFilter(SearchBox.Text);
            StatusBar.Message = $"Deleted '{card.Title}'.";
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            MeetVault.Core.AppLog.Error("delete: meeting " + card.Id + " failed", ex);
            StatusBar.Message = "Could not delete the meeting.";
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
    }

    private void ApplyFilter(string query)
    {
        var term = query.Trim();
        var rows = AppStore.Cards().Where(c => term.Length == 0 || c.Title.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var c in rows)
        {
            c.ShowSelect = _selection.IsActive;
            c.IsSelected = _selection.IsActive && _selection.Contains(c.Id);
        }
        MeetingList.ItemsSource = rows;
        UpdateToolbar();

        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyState.Visibility == Visibility.Visible)
            EmptyState.Text = term.Length == 0
                ? "No meetings yet. Start one to populate this list."
                : $"No meetings match \"{term}\".";
    }
}

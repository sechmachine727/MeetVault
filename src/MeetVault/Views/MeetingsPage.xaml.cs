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
    public MeetingCard(string id, string title, string createdAt, string duration, string status, bool isDraft)
    {
        Id = id; Title = title; CreatedAt = createdAt; Duration = duration; Status = status; IsDraft = isDraft;
    }

    public string Id { get; }
    public string Title { get; }
    public string CreatedAt { get; }
    public string Duration { get; }
    public string Status { get; }
    public bool IsDraft { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Still-recording or resumable-draft meetings cannot be bulk-deleted from
    // these lists; the Core repository refuses them as well (one rule, both layers).
    public bool IsSelectable => !IsDraft;
    public string SelectReason => IsDraft ? Loc.T("Mtg.DraftReason") : "";

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
        ? (IsSelectable ? Loc.T("Mtg.RowToggle") : SelectReason)
        : Loc.T("Mtg.RowOpen");

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
        MeetingStatus.Recording => Loc.T("Mtg.StatusRecording"),
        MeetingStatus.Paused => Loc.T("Mtg.StatusPaused"),
        MeetingStatus.Processing => Loc.T("Mtg.StatusProcessing"),
        MeetingStatus.Ready => Loc.T("Mtg.StatusReady"),
        MeetingStatus.Failed => Loc.T("Mtg.StatusFailed"),
        _ => Loc.T("Mtg.StatusUnknown"),
    };

    private static string Duration(Meeting m)
    {
        if (m.EndedAt is not { } ended) return Loc.T("Mtg.DurInProgress");
        var minutes = (int)Math.Round((ended - m.StartedAt).TotalMinutes);
        return minutes < 60 ? Loc.Tf("Mtg.DurMin", minutes) : Loc.Tf("Mtg.DurHourMin", minutes / 60, minutes % 60);
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
        StatusBar.Message = Loc.Tf("Mtg.FromRepo", AppStore.Cards().Count);
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
        StatusBar.Message = Loc.T("Mtg.SelectHint");
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
        // The search filter may hide selected rows; the delete still covers the whole
        // selection, so the confirmation says how many hidden meetings go with it.
        var visibleIds = VisibleRows.Select(c => c.Id).ToHashSet();
        var hidden = ids.Count(id => !visibleIds.Contains(id));
        var hiddenNote = hidden > 0 ? Loc.Tf("Mtg.HiddenNote", hidden) : "";
        var confirm = new ContentDialog
        {
            Title = Loc.T("Mtg.DelTitle"),
            Content = Loc.Tf("Mtg.DelBody", ids.Length) + hiddenNote,
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
            StatusBar.Message = Loc.Tf("Mtg.Deleted", result.DeletedIds.Count);
            StatusBar.Severity = InfoBarSeverity.Success;
        }
        else
        {
            ApplyFilter(SearchBox.Text);
            StatusBar.Message = Loc.Tf("Mtg.Partial", result.DeletedIds.Count, result.FailedIds.Count);
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
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        SelectionCount.Text = Loc.Tf("Mtg.SelectedCount", _selection.Count);
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
            Title = Loc.T("Mtg.DelOneTitle"),
            Content = Loc.Tf("Mtg.DelOneBody", card.Title),
            PrimaryButtonText = Loc.T("Mtg.DelConfirm"),
            CloseButtonText = Loc.T("Mtg.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var media = AppStore.Repo.DeleteMeeting(card.Id);
            if (media is { Length: > 0 })
            {
                if (!AppStore.Repo.IsManagedMedia(media))
                {
                    MeetVault.Core.AppLog.Warn("delete: refused to remove media outside managed directory " + media);
                }
                else
                {
                    try { if (File.Exists(media)) File.Delete(media); }
                    catch (Exception ex) { MeetVault.Core.AppLog.Warn("delete: could not remove media " + media + " :: " + ex.Message); }
                }
            }
            ApplyFilter(SearchBox.Text);
            StatusBar.Message = Loc.Tf("Mtg.DeletedOne", card.Title);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            MeetVault.Core.AppLog.Error("delete: meeting " + card.Id + " failed", ex);
            StatusBar.Message = Loc.T("Mtg.CouldNotDelete");
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
    }

    // Content column: fill up to a 1100 DIP reading width and stay centered in the page.
    // (Stretch + MaxWidth does not center reliably in WinUI 3; an explicit clamped width is deterministic.)
    private void RootScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        => ContentColumn.Width = Math.Min(1100, e.NewSize.Width);

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
                ? Loc.T("Mtg.EmptyList")
                : Loc.Tf("Mtg.NoMatch", term);
    }
}

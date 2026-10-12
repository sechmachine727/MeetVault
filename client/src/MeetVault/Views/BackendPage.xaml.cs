using System.Runtime.InteropServices;
using MeetVault.Core;
using MeetVault.Core.Backend;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

// Server transcript / analysis / Q&A workspace for one synced meeting. Everything renders from the
// backend (the source of truth) with an on-disk cache for instant open; the local recording is the
// default player source.
public sealed partial class BackendPage : Page
{
    public sealed class LineRow
    {
        public string Speaker { get; set; } = "";
        public string Time { get; set; } = "";
        public string Text { get; set; } = "";
        public long StartMs { get; set; }
    }

    public sealed class NameRow
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Detail { get; set; } = "";
        public long? StartMs { get; set; }
        public List<string> SegmentIds { get; set; } = new();
    }

    private readonly BackendCache _cache = new(BackendCache.DefaultRoot);
    private readonly List<(Meeting Meeting, BackendSyncRow Sync)> _synced = new();
    private readonly List<BackendSegment> _segments = new();
    private bool _suppressPicker;
    private bool _busy;
    private Meeting? _meeting;
    private string? _backendId;

    public BackendPage()
    {
        InitializeComponent();
        ReloadPicker();
        Unloaded += (_, _) => { try { BackendPlayer.Source = null; } catch { } };
    }

    private void ReloadPicker()
    {
        _synced.Clear();
        _synced.AddRange(AppStore.Repo.ListSyncedMeetings());
        _suppressPicker = true;
        try
        {
            BackendMeetingPicker.ItemsSource = _synced
                .Select(s => s.Meeting.Title + " — " + s.Meeting.StartedAt.ToString("g")).ToArray();
            if (_synced.Count == 0)
            {
                BackendMeetingPicker.SelectedIndex = -1;
                EmptyState.Visibility = Visibility.Visible;
                ContentPanel.Visibility = Visibility.Collapsed;
                return;
            }
            var wanted = PendingBackend.MeetingId;
            PendingBackend.MeetingId = null;
            var index = wanted is null ? 0 : Math.Max(0, _synced.FindIndex(s => s.Meeting.Id == wanted));
            BackendMeetingPicker.SelectedIndex = index;
        }
        finally { _suppressPicker = false; }
        _ = LoadAsync();
    }

    private void Meeting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPicker) return;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        var index = BackendMeetingPicker.SelectedIndex;
        if (index < 0 || index >= _synced.Count) return;
        _meeting = _synced[index].Meeting;
        _backendId = _synced[index].Sync.BackendId;
        EmptyState.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;
        BackendStateText.Text = Loc.T(BackendPresentation.StatusKey(_synced[index].Sync.State));
        BackendMockBadge.Visibility = _synced[index].Sync.Mock ? Visibility.Visible : Visibility.Collapsed;
        if (_backendId is null) { return; }
        _busy = true;
        try
        {
            using var transport = new BackendHttpTransport(
                (AppStore.Repo.GetSetting("backend.url") ?? "").Trim(),
                AppStore.Secrets.Get("BackendApiKey") ?? "");
            var client = new BackendClient(transport);
            var status = await client.GetStatusAsync(_backendId);
            RenderStatus(status);
            try
            {
                var transcript = await client.GetActiveTranscriptAsync(_backendId);
                RenderTranscript(transcript);
                _cache.SaveTranscript(_meeting.Id, await client.GetTranscriptRawAsync(_backendId));
            }
            catch (BackendApiException ex) { AppLog.Warn("backend: transcript fetch failed: " + ex.Code); }
            try
            {
                var bundle = await client.GetAnalysisAsync(_backendId);
                RenderAnalysis(bundle);
                _cache.SaveAnalysis(_meeting.Id, await client.GetAnalysisRawAsync(_backendId));
            }
            catch (BackendApiException ex) { AppLog.Warn("backend: analysis fetch failed: " + ex.Code); }
            try { RenderSpeakers(await client.ListSpeakersAsync(_backendId)); } catch (BackendApiException) { }
            try { RenderVersions(await client.ListTranscriptsAsync(_backendId)); } catch (BackendApiException) { }
            try { RenderJobs(await client.ListJobsAsync(_backendId)); } catch (BackendApiException) { }
            try { RenderMedia(await client.ListMediaAsync(_backendId)); } catch (BackendApiException) { }
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = Loc.Tf("Back.LoadFailed", ex is BackendApiException api ? api.Code + ": " + api.Message : ex.Message);
            StatusBar.IsOpen = true;
        }
        finally { _busy = false; }
    }

    private void RenderStatus(BackendStatus status)
    {
        BackendStateText.Text = Loc.T(BackendPresentation.StatusKey(status.Status));
        BackendMockBadge.Visibility = status.Mock ? Visibility.Visible : Visibility.Collapsed;
        BackendStepsText.Text = BackendPresentation.StepsSummary(status.Steps);
        BackendStaleText.Text = status.StaleKinds.Count > 0
            ? Loc.Tf("Back.Stale", string.Join(", ", status.StaleKinds)) : "";
        BackendProgressBar.Visibility = status.Status == "processing" || status.Status == "uploaded"
            ? Visibility.Visible : Visibility.Collapsed;
        BackendProgressBar.Value = BackendPresentation.Percent(status.Progress);
    }

    private void RenderTranscript(BackendTranscriptDetail transcript)
    {
        _segments.Clear();
        _segments.AddRange(transcript.Segments);
        TranscriptList.ItemsSource = transcript.Segments
            .Select(s => new LineRow
            {
                Speaker = s.SpeakerLabel,
                Time = BackendPresentation.SpeakerLine(s.StartMs, "").TrimStart('[').TrimEnd(']', ' '),
                Text = s.Text,
                StartMs = s.StartMs,
            })
            .ToArray();
    }

    private void RenderAnalysis(BackendBundle bundle)
    {
        if (bundle.Mock) BackendMockBadge.Visibility = Visibility.Visible;
        SummaryText.Text = bundle.Summary?.Text ?? "";
        SummaryBullets.ItemsSource = (bundle.Summary?.Bullets ?? new List<string>()).Select(b => "• " + b).ToArray();
        AgendaList.ItemsSource = bundle.Sections
            .OrderBy(s => s.Ordinal)
            .Select(s => new NameRow
            {
                Text = s.Title,
                Detail = BackendPresentation.SpeakerLine(s.StartMs, "") + " – " + BackendPresentation.SpeakerLine(s.EndMs, "").TrimEnd(' ', ']').TrimStart('[') + "  " + s.Summary,
                StartMs = s.StartMs,
            }).ToArray();
        DecisionsList.ItemsSource = bundle.Decisions.Select(d => new NameRow
        {
            Text = d.Text,
            Detail = string.Join(" · ", new[] { d.AtMs is { } at ? BackendPresentation.SpeakerLine(at, "").Trim() : "" , string.Join(", ", d.DecidedBy) }.Where(x => x.Length > 0)),
            SegmentIds = d.SegmentIds,
        }).ToArray();
        ActionsList.ItemsSource = bundle.Actions.Select(a => new NameRow
        {
            Text = a.Text,
            Detail = string.Join(" · ", new[] { a.Owner, a.Due is { Length: > 0 } due ? a.Due : "" }.Where(x => x.Length > 0)),
            SegmentIds = a.SegmentIds,
        }).ToArray();
        QaList.ItemsSource = bundle.Qa.Select(q => new NameRow
        {
            Text = q.Question,
            Detail = q.Answer + (q.Citations.Count > 0
                ? "\n" + string.Join("\n", q.Citations.Select(c => BackendPresentation.SpeakerLine(c.StartMs, "") + " " + c.Text))
                : ""),
            StartMs = q.Citations.Count > 0 ? q.Citations[0].StartMs : null,
        }).ToArray();
    }

    private void RenderSpeakers(List<BackendSpeaker> speakers) { /* T74 */ }
    private void RenderVersions(List<BackendTranscript> versions) { /* T75 */ }
    private void RenderJobs(List<BackendJob> jobs) { /* T75 */ }
    private void RenderMedia(List<BackendMediaAsset> media) { /* T75 */ }

    private void PlaySource(string path)
    {
        try
        {
            BackendPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
            BackendPlayer.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { AppLog.Warn("backend: play failed: " + ex.Message); }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting?.MediaPath is { } local && File.Exists(local)) { PlaySource(local); return; }
        var mediaDir = Path.Combine(BackendCache.DefaultRoot, "media");
        var downloaded = Directory.Exists(mediaDir)
            ? Directory.EnumerateFiles(mediaDir).FirstOrDefault()
            : null;
        if (downloaded is not null) { PlaySource(downloaded); return; }
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = Loc.T("Back.NoLocalRecording");
        StatusBar.IsOpen = true;
    }

    private void TranscriptRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not LineRow row) return;
        SeekPlayer(row.StartMs);
    }

    private void CitationRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NameRow row) return;
        var ms = row.StartMs;
        if (ms is null && row.SegmentIds.Count > 0)
            ms = _segments.FirstOrDefault(s => s.Id == row.SegmentIds[0])?.StartMs;
        if (ms is { } start) SeekPlayer(start);
    }

    private void SeekPlayer(long startMs)
    {
        try
        {
            var session = BackendPlayer.MediaPlayer?.PlaybackSession;
            if (session is not null && BackendPlayer.Source is not null)
                session.Position = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
        }
        catch { }
    }

    private async void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (_backendId is null) return;
        try
        {
            using var transport = new BackendHttpTransport(
                (AppStore.Repo.GetSetting("backend.url") ?? "").Trim(),
                AppStore.Secrets.Get("BackendApiKey") ?? "");
            var client = new BackendClient(transport);
            var text = await client.GetActiveTranscriptTextAsync(_backendId);
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Status(InfoBarSeverity.Success, Loc.T("Back.Copied"));
        }
        catch (COMException) { Status(InfoBarSeverity.Error, Loc.T("Det.ClipboardBusy")); }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Status(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    // Rerun/Update/Ask/Remove/Speaker/Version/Job/Media handlers arrive in T73–T75; each stays
    // stubbed as `private void Name_Click(object sender, RoutedEventArgs e) { }` until its task.
    private void Rerun_Click(object sender, RoutedEventArgs e) { }
    private void UpdateTranscript_Click(object sender, RoutedEventArgs e) { }
    private void Remove_Click(object sender, RoutedEventArgs e) { }
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
}

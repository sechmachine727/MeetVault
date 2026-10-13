using System.Runtime.InteropServices;
using MeetVault.Core;
using MeetVault.Core.Backend;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

// Server transcript / analysis / Q&A workspace for one synced meeting. Everything renders from the
// backend (the source of truth); the on-disk cache is written on every successful load and at
// terminal status — reading it back for offline open is future work. The local recording is the
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
    private readonly Dictionary<string, string> _downloaded = new(StringComparer.Ordinal);
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
        _downloaded.Clear(); // a previous meeting's download must never be played as this one's
        var index = BackendMeetingPicker.SelectedIndex;
        if (index < 0 || index >= _synced.Count) return;
        _meeting = _synced[index].Meeting;
        _backendId = _synced[index].Sync.BackendId;
        EmptyState.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;
        BackendStateText.Text = Loc.T(BackendPresentation.StatusKey(_synced[index].Sync.State));
        BackendMockBadge.Visibility = _synced[index].Sync.Mock ? Visibility.Visible : Visibility.Collapsed;
        if (_backendId is null)
        {
            // Never show the previous meeting's server data for a row without a backend id.
            _segments.Clear();
            TranscriptList.ItemsSource = Array.Empty<LineRow>();
            SummaryText.Text = "";
            SummaryBullets.ItemsSource = Array.Empty<string>();
            AgendaList.ItemsSource = Array.Empty<NameRow>();
            DecisionsList.ItemsSource = Array.Empty<NameRow>();
            ActionsList.ItemsSource = Array.Empty<NameRow>();
            QaList.ItemsSource = Array.Empty<NameRow>();
            SpeakersList.ItemsSource = Array.Empty<SpeakerRow>();
            VersionsList.ItemsSource = Array.Empty<NameRow>();
            JobsList.ItemsSource = Array.Empty<NameRow>();
            MediaList.ItemsSource = Array.Empty<NameRow>();
            BackendStepsText.Text = "";
            BackendStaleText.Text = "";
            BackendProgressBar.Value = 0;
            BackendProgressBar.Visibility = Visibility.Collapsed;
            BackendPlayer.Source = null;
            QaStatus.Text = "";
            EventsText.Text = "";
            QaBox.Text = "";
            return;
        }
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
            catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
            try
            {
                var bundle = await client.GetAnalysisAsync(_backendId);
                RenderAnalysis(bundle);
                _cache.SaveAnalysis(_meeting.Id, await client.GetAnalysisRawAsync(_backendId));
            }
            catch (BackendApiException ex) { AppLog.Warn("backend: analysis fetch failed: " + ex.Code); }
            catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
            try { RenderSpeakers(await client.ListSpeakersAsync(_backendId)); } catch (BackendApiException ex) { AppLog.Warn("backend: speakers failed: " + ex.Code); }
            catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
            try { RenderVersions(await client.ListTranscriptsAsync(_backendId)); } catch (BackendApiException ex) { AppLog.Warn("backend: versions failed: " + ex.Code); }
            catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
            try { RenderJobs(await client.ListJobsAsync(_backendId)); } catch (BackendApiException ex) { AppLog.Warn("backend: jobs failed: " + ex.Code); }
            catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
            try { RenderMedia(await client.ListMediaAsync(_backendId)); } catch (BackendApiException ex) { AppLog.Warn("backend: media failed: " + ex.Code); }
            catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
            StatusBar.IsOpen = false; // a previous error must not linger after a later successful load
        }
        catch (Exception ex)
        {
            Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex)));
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

    private readonly List<BackendSpeaker> _speakerRows = new();

    private void RenderSpeakers(List<BackendSpeaker> speakers)
    {
        _speakerRows.Clear();
        _speakerRows.AddRange(speakers);
        SpeakersList.ItemsSource = _speakerRows
            .Select(s => new SpeakerRow
            {
                Id = s.Id,
                Label = s.Label,
                Stats = Loc.Tf("Back.SpeakerStats", s.SegmentCount, (s.SpeakingMs / 1000).ToString()),
                Preview = s.Preview,
            })
            .ToArray();
    }

    public sealed class SpeakerRow
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Stats { get; set; } = "";
        public string Preview { get; set; } = "";
        public string? MergeTarget { get; set; }
    }
    private readonly List<BackendJob> _jobRows = new();
    private readonly List<BackendMediaAsset> _mediaRows = new();

    private void RenderVersions(List<BackendTranscript> versions)
    {
        VersionsList.ItemsSource = versions
            .OrderByDescending(v => v.Version)
            .Select(v => new NameRow
            {
                Id = v.Id,
                Text = Loc.Tf("Back.VersionLine", v.Version, v.Source, v.Provider, v.SegmentCount) + (v.IsActive ? " · " + Loc.T("Back.Active") : ""),
                Detail = v.IsMock ? Loc.T("Back.MockBadge") : "",
            })
            .ToArray();
    }

    private void RenderJobs(List<BackendJob> jobs)
    {
        _jobRows.Clear();
        _jobRows.AddRange(jobs);
        JobsList.ItemsSource = _jobRows
            .Select(j => new NameRow
            {
                Id = j.Id,
                Text = j.Type + " · " + j.State + " · " + BackendPresentation.Percent(j.Progress) + "%"
                    + (j.Error is { Length: > 0 } err ? " — " + err : ""),
                Detail = j.IsMock ? Loc.T("Back.MockBadge") : "",
            })
            .ToArray();
    }

    private void RenderMedia(List<BackendMediaAsset> media)
    {
        _mediaRows.Clear();
        _mediaRows.AddRange(media);
        MediaList.ItemsSource = _mediaRows
            .Select(m => new NameRow
            {
                Id = m.Id,
                Text = m.Kind + " · " + m.OriginalName + " · " + (m.DurationMs / 1000) + "s · " + (m.SizeBytes / 1024 / 1024) + " MB",
                Detail = m.Kind == "video" ? "offset " + m.StartOffsetMs + " ms" : "",
            })
            .ToArray();
    }

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
        var downloaded = _downloaded.Values.FirstOrDefault(File.Exists);
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
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Status(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private static string DescribeBackendError(Exception ex) => ex switch
    {
        BackendApiException api => api.Code + ": " + api.Message,
        ArgumentException argument => argument.Message.Contains("key", StringComparison.OrdinalIgnoreCase) ? Loc.T("Det.BackendNoKey") : Loc.T("Det.BackendNoUrl"),
        TaskCanceledException => Loc.T("Det.BackendTimeout"),
        _ => Loc.T("Det.BackendUnreachable"),
    };

    private async void UpdateTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_backendId is null || _meeting is null) return;
        if (!TryClientUrl(out var url, out var key)) { Status(InfoBarSeverity.Warning, Loc.T(url.Length == 0 ? "Det.BackendNoUrl" : "Det.BackendNoKey")); return; }
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            var segments = AppStore.Repo.LoadSegments(_meeting.Id);
            if (!segments.Any(s => !string.IsNullOrWhiteSpace(s.Text)))
            {
                Status(InfoBarSeverity.Warning, Loc.T("Det.EmptyTranscript"));
                return;
            }
            var media = await client.ListMediaAsync(_backendId);
            var probedMs = media.FirstOrDefault(m => m.Kind == "audio")?.DurationMs;
            var alignMs = _meeting.EndedAt is { } endedAt ? Math.Max(0, (long)(endedAt - _meeting.StartedAt).TotalMilliseconds) : 0;
            var formatA = BackendFormatter.ToFormatA(_meeting,
                segments, AppStore.Repo.LoadSpeakers(_meeting.Id), alignMs > 0 ? alignMs : probedMs);
            await client.UploadTranscriptAsync(_backendId, formatA, _meeting.Language);
            await client.ProcessAsync(_backendId, new List<string> { "analyze" }, force: true);
            Status(InfoBarSeverity.Informational, Loc.T("Back.RerunStarted"));
            await PollUntilTerminalAsync(client);
            await LoadAsync();
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }

    private async void Rerun_Click(object sender, RoutedEventArgs e)
    {
        if (_backendId is null) return;
        if (!TryClientUrl(out var url, out var key)) { Status(InfoBarSeverity.Warning, Loc.T(url.Length == 0 ? "Det.BackendNoUrl" : "Det.BackendNoKey")); return; }
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.ProcessAsync(_backendId, new List<string> { "analyze" }, force: true);
            Status(InfoBarSeverity.Informational, Loc.T("Back.RerunStarted"));
            await PollUntilTerminalAsync(client);
            await LoadAsync();
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }

    private async Task<BackendStatus> PollUntilTerminalAsync(BackendClient client)
    {
        // Bounded wait (review 4), mirroring WaitForJobAsync: a stuck pipeline must not keep the
        // poll loop (and the Re-run / Update-transcript handlers) running forever; after the
        // deadline the last observed status comes back.
        var deadline = DateTime.UtcNow.AddMinutes(10);
        var id = _backendId!; // a picker switch mid-poll must not make the loop follow the new meeting
        var status = await client.GetStatusAsync(id);
        RenderStatus(status);
        while (status.Status is not ("ready" or "failed" or "deleted") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            status = await client.GetStatusAsync(id);
            RenderStatus(status);
        }
        return status;
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_backendId is null || _meeting is null) return;
        var confirm = new ContentDialog
        {
            Title = Loc.T("Back.RemoveTitle"),
            Content = Loc.T("Back.RemoveBody"),
            PrimaryButtonText = Loc.T("Back.Remove"),
            CloseButtonText = Loc.T("Det.Cancel"),
            XamlRoot = XamlRoot,
        };
        if (!TryClientUrl(out var url, out var key)) return;
        try
        {
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.DeleteMeetingAsync(_backendId);
            AppStore.Repo.ClearBackendSync(_meeting.Id);
            Status(InfoBarSeverity.Success, Loc.T("Back.Removed"));
            ReloadPicker();
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }
    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_backendId is null || _meeting is null) return;
        var question = QaBox.Text.Trim();
        if (question.Length == 0) return;
        if (!TryClientUrl(out var url, out var key)) { Status(InfoBarSeverity.Warning, Loc.T(url.Length == 0 ? "Det.BackendNoUrl" : "Det.BackendNoKey")); return; }
        AskButton.IsEnabled = false;
        QaStatus.Text = Loc.T("Back.AskQueued");
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            var job = await client.AskAsync(_backendId, question);
            var terminal = await WaitForJobAsync(client, job.JobId);
            QaStatus.Text = terminal.State == "succeeded" ? Loc.T("Back.State.Ready") : Loc.T("Back.State.Failed");
            if (terminal.State == "succeeded")
            {
                QaBox.Text = "";
                var bundle = await client.GetAnalysisAsync(_backendId);
                RenderAnalysis(bundle);
            }
            else
            {
                QaStatus.Text = Loc.Tf("Back.LoadFailed", terminal.Error ?? terminal.State);
            }
        }
        catch (BackendApiException ex) { QaStatus.Text = Loc.Tf("Back.LoadFailed", ex.Code + ": " + ex.Message); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
        finally { AskButton.IsEnabled = true; }
    }

    private static bool TryClientUrl(out string url, out string key)
    {
        url = (AppStore.Repo.GetSetting("backend.url") ?? "").Trim();
        key = AppStore.Secrets.Get("BackendApiKey") ?? "";
        return url.Length > 0 && key.Length > 0;
    }

    private async Task<BackendJobDetail> WaitForJobAsync(BackendClient client, string jobId)
    {
        // Bounded wait (audit fix): a job stuck queued (worker down) must not spin forever; after the
        // deadline the last observed job comes back and the caller reports its state.
        var deadline = DateTime.UtcNow.AddMinutes(10);
        var job = await client.GetJobAsync(jobId);
        while (job.State is not ("succeeded" or "failed" or "cancelled") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            job = await client.GetJobAsync(jobId);
        }
        return job;
    }
    private void SpeakerMergeCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.DataContext is not SpeakerRow row) return;
        combo.ItemsSource = _speakerRows.Where(s => s.Id != row.Id).Select(s => s.Label).ToArray();
    }

    private void SpeakerMergeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.DataContext is SpeakerRow row && combo.SelectedItem is string label)
            row.MergeTarget = label;
    }

    private async void SpeakerSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SpeakerRow row || _backendId is null) return;
        if (row.Label.Trim().Length == 0) return;
        if (!TryClientUrl(out var url, out var key)) return;
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.RenameSpeakerAsync(row.Id, row.Label.Trim());
            RenderSpeakers(await client.ListSpeakersAsync(_backendId));
            RenderTranscript(await client.GetActiveTranscriptAsync(_backendId));
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }

    private async void SpeakerMerge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SpeakerRow row
            || row.MergeTarget is not { Length: > 0 } targetLabel || _backendId is null) return;
        var target = _speakerRows.FirstOrDefault(s => s.Label == targetLabel);
        if (target is null) return;
        if (!TryClientUrl(out var url, out var key)) return;
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.MergeSpeakersAsync(row.Id, target.Id);
            RenderSpeakers(await client.ListSpeakersAsync(_backendId));
            RenderTranscript(await client.GetActiveTranscriptAsync(_backendId));
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }

    private async void SpeakerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SpeakerRow row || _backendId is null) return;
        var confirm = new ContentDialog
        {
            Title = Loc.T("Back.SpeakerDeleteTitle"),
            Content = Loc.Tf("Back.SpeakerDeleteBody", row.Label),
            PrimaryButtonText = Loc.T("Back.Delete"),
            CloseButtonText = Loc.T("Det.Cancel"),
            XamlRoot = XamlRoot,
        };
        if (!TryClientUrl(out var url, out var key)) return;
        try
        {
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.DeleteSpeakerAsync(row.Id);
            RenderSpeakers(await client.ListSpeakersAsync(_backendId));
            RenderTranscript(await client.GetActiveTranscriptAsync(_backendId));
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }
    private async void VersionActivate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NameRow row || _backendId is null) return;
        if (!TryClientUrl(out var url, out var key)) return;
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.ActivateTranscriptAsync(row.Id);
            await LoadAsync();
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }

    private async void VersionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NameRow row || _backendId is null) return;
        if (!TryClientUrl(out var url, out var key)) return;
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.DeleteTranscriptAsync(row.Id);
            await LoadAsync();
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }

    private async void JobWatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NameRow row) return;
        if (!TryClientUrl(out var url, out var key)) return;
        EventsText.Text = "";
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await foreach (var jobEvent in client.WatchJobAsync(row.Id))
                EventsText.Text += jobEvent.Level + ": " + jobEvent.Message + "\n";
        }
        catch (Exception ex) { EventsText.Text += ex.Message; }
    }

    private async void JobCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NameRow row) return;
        if (!TryClientUrl(out var url, out var key)) return;
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.CancelJobAsync(row.Id);
            await LoadAsync();
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }

    private async void MediaDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NameRow row) return;
        if (!TryClientUrl(out var url, out var key)) return;
        var asset = _mediaRows.FirstOrDefault(m => m.Id == row.Id);
        if (asset is null) return;
        var extension = Path.GetExtension(asset.OriginalName);
        var destination = _cache.MediaPath(asset.Id, extension);
        Status(InfoBarSeverity.Informational, Loc.T("Back.Downloading"));
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            using var downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
            var client = new BackendClient(transport);
            await client.DownloadMediaAsync(asset.Id, destination, downloadCts.Token);
            _downloaded[asset.Id] = destination;
            Status(InfoBarSeverity.Success, Loc.Tf("Back.Downloaded", destination));
        }
        catch (BackendApiException ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", ex.Code)); }
        catch (Exception ex) { Status(InfoBarSeverity.Error, Loc.Tf("Back.LoadFailed", DescribeBackendError(ex))); }
    }
}

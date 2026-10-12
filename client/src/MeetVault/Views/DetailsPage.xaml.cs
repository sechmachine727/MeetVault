using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeetVault.Core;
using MeetVault.Core.Backend;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;

namespace MeetVault.Views;

public sealed partial class DetailsPage : Page
{
    public ObservableCollection<TranscriptLine> Lines { get; } = new();
    public ObservableCollection<string> Speakers { get; } = new();

    private readonly List<string> _originalText = new();
    private Meeting? _meeting;
    // Parallel maps so the UI round-trips edits to the repo.
    private readonly List<string> _segmentIds = new();
    private readonly List<string> _segmentSpeakerIds = new(); // original speaker id per line: safe fallback on save
    private readonly List<(string Id, long StartMs, long EndMs)> _timings = new();
    private readonly Dictionary<string, string> _speakerIdByName = new();
    // The meeting's speaker rows decide whether names/pickers show at all: a meeting
    // recorded without diarization has none and must render nameless (no spk_N fallback).
    private bool _hasSpeakers;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    private List<MediaTrack> _videoTracks = new();
    private MediaTrack? _videoTrack;
    private int _highlightIndex = -1;
    // --- Backend processing ---
    private bool _backendBusy;
    private CancellationTokenSource? _backendCts;
    private string? _backendId;
    private string? _backendActiveJobId;

    public DetailsPage()
    {
        InitializeComponent();
        // Code-set, not XAML: the job placeholder must localize even though ShowPriorJob
        // returns before overwriting it when there is no job (a literal would show English).
        JobState.Text = Loc.T("Det.NoJobSent");

        var targetId = PendingDetails.MeetingId;
        PendingDetails.MeetingId = null;
        _meeting = targetId is null ? AppStore.Repo.ListMeetings().FirstOrDefault() : AppStore.Repo.Load(targetId);
        if (_meeting is null)
        {
            MeetingTitle.Text = Loc.T("Det.NoMeetingSelected");
            UpdateLineStatus();
            SetActionsEnabled(false);
            EmptyState.Text = Loc.T("Det.NoMeeting");
            EmptyState.Visibility = Visibility.Visible;
            TranscriptList.Visibility = Visibility.Collapsed;
            return;
        }

        MeetingTitle.Text = _meeting.Title;
        var progressLabel = Loc.T("Mtg.DurInProgress");
        MeetingMeta.Text = _meeting.EndedAt is { } end
            ? $"{_meeting.StartedAt:g}  \u00b7  {FormatDuration(_meeting)}  \u00b7  {MeetingCards.StatusLabel(_meeting.Status)}"
            : $"{_meeting.StartedAt:g}  \u00b7  {progressLabel}  \u00b7  {MeetingCards.StatusLabel(_meeting.Status)}";

        var repoSegs = AppStore.Repo.LoadSegments(_meeting.Id).ToList();
        var repoSpks = AppStore.Repo.LoadSpeakers(_meeting.Id).ToList();
        _hasSpeakers = repoSpks.Count > 0;
        foreach (var s in repoSpks) { _speakerIdByName[s.DisplayName] = s.Id; Speakers.Add(s.DisplayName); }
        if (Speakers.Count == 0) Speakers.Add(Loc.T("Det.SpeakerFallback"));
        var names = repoSpks.ToDictionary(s => s.Id, s => s.DisplayName);
        foreach (var s in repoSegs)
        {
            Lines.Add(new TranscriptLine(repoSpks.Count == 0 ? "" : (names.TryGetValue(s.SpeakerId, out var n) ? n : s.SpeakerId), s.Text));
            _originalText.Add(s.Text);
            _segmentIds.Add(s.Id);
            _segmentSpeakerIds.Add(s.SpeakerId);
            _timings.Add((s.Id, s.StartMs, s.EndMs));
        }

        // Array snapshot across the ABI; items mutate in place so no re-set is needed after load.
        TranscriptList.ItemsSource = Lines.ToArray();
        ShowPlayer();
        ShowVideo();
        SubscribePlayhead();
        ShowPriorJob(_meeting);
        ShowPriorBackend();
        UpdateLineStatus();
        Unloaded += (_, _) =>
        {
            try { Player.Source = null; } catch { }
            try { VideoPlayer.Source = null; } catch { }
            try { _backendCts?.Cancel(); } catch { } // the poll stops with the page (plan decision #8)
        };
    }

    private static string FormatDuration(Meeting m)
    {
        if (m.EndedAt is not { } ended) return Loc.T("Mtg.DurInProgress");
        var minutes = (int)Math.Round((ended - m.StartedAt).TotalMinutes);
        return minutes < 60 ? Loc.Tf("Mtg.DurMin", minutes) : Loc.Tf("Mtg.DurHourMin", minutes / 60, minutes % 60);
    }

    private void ShowPlayer()
    {
        if (_meeting?.MediaPath is not { } path || !File.Exists(path)) return;
        try
        {
            Player.Source = MediaSource.CreateFromUri(new Uri(path));
            Player.Visibility = Visibility.Visible;
        }
        catch { Player.Visibility = Visibility.Collapsed; }
    }

    // The playhead subscription drives BOTH the row highlight (every meeting) and the
    // video follow (when tracks exist). It used to sit below ShowVideo's early return, so
    // meetings without screen tracks lost the highlight entirely (review r6).
    private void SubscribePlayhead()
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;
        session.PositionChanged += OnAudioPositionChanged;
        session.PlaybackStateChanged += OnAudioPlaybackStateChanged;
    }

    // E2: screen tracks (every run with a live file) play in one element. The AUDIO player
    // stays the master; the video follows it, shifted per run. Pause/resume produces
    // MULTIPLE files, so the track covering the playhead is picked dynamically (review r5:
    // binding the first track alone left every later run frozen past its end).
    private void ShowVideo()
    {
        if (_meeting is null) return;
        _videoTracks = AppStore.Repo.ListMediaTracks(_meeting.Id)
            .Where(t => t.Kind == "screen" && File.Exists(t.Path))
            .OrderBy(t => t.StartOffsetMs).ThenBy(t => t.CreatedAt, StringComparer.Ordinal)
            .ToList();
        if (_videoTracks.Count == 0) return;
        SetVideoTrack(_videoTracks[0], 0);
    }

    // Swap the element to the run that covers the playhead and seek it to the right spot
    // once its media opens (mirroring the audio play state).
    private void SetVideoTrack(MediaTrack track, double playheadMs)
    {
        _videoTrack = track;
        try
        {
            VideoPlayer.Source = MediaSource.CreateFromUri(new Uri(track.Path));
            VideoPlayer.Visibility = Visibility.Visible;
            if (VideoPlayer.MediaPlayer is not { } vmp) return;
            vmp.IsMuted = true; // video-only file: the audio master is the sound
            var target = TimeSpan.FromMilliseconds(Math.Max(0, playheadMs - track.StartOffsetMs));
            Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlayer, object>? opened = null;
            opened = (_, _) =>
            {
                try
                {
                    if (vmp.PlaybackSession is { } vs) vs.Position = target;
                    var audio = Player.MediaPlayer?.PlaybackSession;
                    if (audio?.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing) vmp.Play();
                }
                catch { }
                vmp.MediaOpened -= opened;
            };
            vmp.MediaOpened += opened;
        }
        catch (Exception ex)
        {
            MeetVault.Core.AppLog.Warn("details: video track failed to load: " + ex.Message);
            VideoPlayer.Visibility = Visibility.Collapsed;
            _videoTrack = null;
        }
    }

    private void OnAudioPositionChanged(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        => DispatcherQueue.TryEnqueue(() => SyncVideoTo(sender.Position));

    private void OnAudioPlaybackStateChanged(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (_videoTrack is null || VideoPlayer.MediaPlayer is not { } vmp) return;
            var session = Player.MediaPlayer?.PlaybackSession;
            if (session is null) return;
            if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing &&
                session.Position.TotalMilliseconds >= _videoTrack.StartOffsetMs)
            { try { vmp.Play(); } catch { } }
            else { try { vmp.Pause(); } catch { } }
        });

    // Keeps the video within 400 ms of the audio playhead (shifted by the active run's
    // offset); before a run's offset the video holds its first frame, paused.
    private void SyncVideoTo(TimeSpan audioPosition)
    {
        var ms = audioPosition.TotalMilliseconds;
        if (_videoTracks.Count > 0)
        {
            var pick = TrackFor(ms);
            if (pick is not null && !ReferenceEquals(pick, _videoTrack)) SetVideoTrack(pick, ms);
        }
        if (_videoTrack is not null && VideoPlayer.MediaPlayer is { } vmp && vmp.PlaybackSession is { } videoSession)
        {
            var target = ms - _videoTrack.StartOffsetMs;
            try
            {
                if (target < 0)
                {
                    if (videoSession.Position != TimeSpan.Zero) videoSession.Position = TimeSpan.Zero;
                }
                else if (Math.Abs(videoSession.Position.TotalMilliseconds - target) > 400)
                {
                    videoSession.Position = TimeSpan.FromMilliseconds(target);
                }
            }
            catch { }
        }
        HighlightSegment(ms);
    }

    // The last run that has started at the playhead; before the first offset and after the
    // last run ends, the boundary run is picked (seek clamps to 0 / its final frame).
    private MediaTrack? TrackFor(double positionMs)
    {
        MediaTrack? pick = null;
        foreach (var t in _videoTracks)
        {
            if (t.StartOffsetMs <= positionMs) pick = t;
            else break;
        }
        return pick ?? (_videoTracks.Count > 0 ? _videoTracks[0] : null);
    }

    // The row whose [StartMs, EndMs) contains the playhead gets the highlight brush; only
    // boundary transitions touch the lines, so the common tick is one comparison.
    private void HighlightSegment(double positionMs)
    {
        var idx = -1;
        for (var i = 0; i < _timings.Count; i++)
        {
            if (positionMs >= _timings[i].StartMs && positionMs < _timings[i].EndMs) { idx = i; break; }
        }
        if (idx == _highlightIndex) return;
        if (_highlightIndex >= 0 && _highlightIndex < Lines.Count) Lines[_highlightIndex].IsActive = false;
        _highlightIndex = idx;
        if (idx >= 0 && idx < Lines.Count) Lines[idx].IsActive = true;
    }

    // Tapping a transcript row seeks the master (audio) player; the video follows.
    private void Row_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TranscriptLine line) return;
        var idx = Lines.IndexOf(line);
        if (idx < 0 || idx >= _timings.Count) return;
        try
        {
            var session = Player.MediaPlayer?.PlaybackSession;
            if (session is not null) session.Position = TimeSpan.FromMilliseconds(_timings[idx].StartMs);
        }
        catch { }
    }

    private void SetActionsEnabled(bool enabled)
    {
        CopyButton.IsEnabled = enabled;
        ExportButton.IsEnabled = enabled;
        SendButton.IsEnabled = enabled;
    }

    // Prior Gemini result survives restarts via the persisted job row.
    private void ShowPriorJob(Meeting? meeting)
    {
        if (meeting is null) return;
        var job = AppStore.Repo.LoadJob(meeting.Id);
        if (job is null) return;
        GeminiExpander.IsExpanded = true;
        JobState.Text = Loc.Tf("Det.LastResult", GeminiStateLabel(job.State), job.Attempts);
        if (job.State == GeminiJobState.Succeeded && job.ResultPath is not null)
        {
            try { JobResult.Text = File.ReadAllText(job.ResultPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { JobResult.Text = Loc.T("Det.ResultUnreadable"); }
        }
        else JobResult.Text = job.LastError ?? Loc.T("Det.NoResult");
    }

    private static string GeminiStateLabel(GeminiJobState state) => state switch
    {
        GeminiJobState.Queued => Loc.T("Det.StateQueued"),
        GeminiJobState.Uploading => Loc.T("Det.StateUploading"),
        GeminiJobState.Succeeded => Loc.T("Det.StateSucceeded"),
        GeminiJobState.Failed => Loc.T("Det.StateFailed"),
        _ => Loc.T("Det.StateUnknown"),
    };

    private static string NowIso() => DateTimeOffset.UtcNow.ToString("o");

    // A queued debounce must be written before the backend serializes the transcript.
    private void FlushEdits()
    {
        if (_saveTimer is null) return;
        _saveTimer.Stop();
        PersistEdits(null, null!);
    }

    private static string StageLabel(BackendSyncStage stage) => stage switch
    {
        BackendSyncStage.Creating => Loc.T("Back.Stage.Create"),
        BackendSyncStage.UploadingAudio => Loc.T("Back.Stage.Audio"),
        BackendSyncStage.UploadingVideo => Loc.T("Back.Stage.Video"),
        BackendSyncStage.UploadingTranscript => Loc.T("Back.Stage.Transcript"),
        _ => Loc.T("Back.Stage.Start"),
    };

    private static string DescribeBackendError(Exception ex) => ex switch
    {
        BackendApiException api => api.Code + ": " + api.Message,
        ArgumentException => Loc.T("Det.BackendNoUrl"),
        _ => Loc.T("Det.BackendUnreachable"),
    };

    // Prior backend state survives restarts via the backend_sync row.
    private void ShowPriorBackend()
    {
        if (_meeting is null) return;
        var sync = AppStore.Repo.LoadBackendSync(_meeting.Id);
        BackendProgress.Visibility = Visibility.Collapsed;
        BackendCancelButton.Visibility = Visibility.Collapsed;
        BackendSteps.Text = "";
        if (sync is null)
        {
            BackendState.Text = Loc.T("Det.BackendNotSent");
            return;
        }
        BackendState.Text = Loc.T(BackendPresentation.StatusKey(sync.State))
            + (sync.Mock ? " · " + Loc.T("Back.MockBadge") : "")
            + (sync.LastError is { Length: > 0 } error ? " — " + error : "");
    }

    private void SpeakerPicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox box || box.DataContext is not TranscriptLine line) return;
        box.Visibility = _hasSpeakers ? Visibility.Visible : Visibility.Collapsed;
        box.ItemsSource = Speakers.ToArray(); // never hand the live collection across the ABI.
        box.SelectedItem = line.Speaker;
    }

    private void SpeakerPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.DataContext is TranscriptLine line && box.SelectedItem is string speaker)
        {
            line.Speaker = speaker;
            UpdateLineStatus();
            ScheduleSave();
        }
    }

    private void LineText_TextChanged(object sender, TextChangedEventArgs e) { UpdateLineStatus(); ScheduleSave(); }

    // Debounced persist: per-keystroke SQLite rewrites would churn; 800 ms of quiet saves once.
    private void ScheduleSave()
    {
        if (_meeting is null || _segmentIds.Count != Lines.Count) return;
        _saveTimer ??= DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(800);
        _saveTimer.Tick -= PersistEdits;
        _saveTimer.Tick += PersistEdits;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PersistEdits(object? sender, object e)
    {
        _saveTimer?.Stop();
        if (_meeting is null || _segmentIds.Count != Lines.Count) return;
        try
        {
            var speakers = AppStore.Repo.LoadSpeakers(_meeting.Id);
            var segments = new List<Segment>(Lines.Count);
            for (var i = 0; i < Lines.Count; i++)
            {
                var speakerId = _speakerIdByName.TryGetValue(Lines[i].Speaker, out var sid)
                    ? sid
                    : speakers.FirstOrDefault(s => s.DisplayName == Lines[i].Speaker)?.Id ?? _segmentSpeakerIds[i];
                segments.Add(new Segment(_segmentIds[i], speakerId, _timings[i].StartMs, _timings[i].EndMs, Lines[i].Text, true));
            }
            AppStore.Repo.Save(_meeting, speakers, segments);
            _originalText.Clear();
            _originalText.AddRange(Lines.Select(l => l.Text));
            UpdateLineStatus();
        }
        catch (Exception ex) { MeetVault.Core.AppLog.Error("details: persist edits failed", ex); Status(InfoBarSeverity.Warning, Loc.Tf("Det.EditSaveFailed", Short(ex))); }
    }

    private void UpdateLineStatus()
    {
        if (_meeting is null) { LineStatus.Text = ""; return; }
        var edited = Lines.Where((line, i) => i < _originalText.Count && line.Text != _originalText[i]).Count();
        LineStatus.Text = Lines.Count == 0
            ? Loc.T("Det.NoLines")
            : edited == 0 ? Loc.Tf("Det.LineCount", Lines.Count) : Loc.Tf("Det.LineCountEdited", Lines.Count, edited);
        EmptyState.Visibility = Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Lines.Count == 0) EmptyState.Text = _meeting is null
            ? Loc.T("Det.NoMeeting")
            : Loc.T("Det.NoTranscriptLines");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTranscript(out var text)) return;
        try
        {
            ToClipboard(text);
            Status(InfoBarSeverity.Success, Loc.Tf("Det.Copied", Lines.Count));
        }
        catch (COMException) { Status(InfoBarSeverity.Error, Loc.T("Det.ClipboardBusy")); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTranscript(out var text)) return;
        var configured = AppStore.Repo.GetSetting("storage.path");
        var folder = string.IsNullOrWhiteSpace(configured) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : configured;
        var path = Path.Combine(folder, FileNameFor(_meeting?.Title ?? "meeting"));
        if (File.Exists(path))
        {
            var confirm = new ContentDialog { Title = Loc.T("Det.OverwriteTitle"), Content = Loc.Tf("Det.OverwriteBody", path), PrimaryButtonText = Loc.T("Det.Overwrite"), CloseButtonText = Loc.T("Det.Cancel"), XamlRoot = XamlRoot };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        try
        {
            Directory.CreateDirectory(folder); // the configured folder may not exist yet
            File.WriteAllText(path, text);
            Status(InfoBarSeverity.Success, Loc.Tf("Det.SavedExport", Lines.Count, path));
            MeetVault.Core.AppLog.Info("details: exported " + Lines.Count + " line(s) to " + path);
        }
        catch (DirectoryNotFoundException)
        {
            Status(InfoBarSeverity.Error, Loc.T("Det.ExportFolderFailed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status(InfoBarSeverity.Error, Loc.Tf("Det.ExportWriteFailed", Short(ex)));
        }
    }

    private async void Gemini_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting is null || !TryTranscript(out _)) return;
        var key = AppStore.Secrets.Get("GeminiApiKey");
        if (key is not { Length: > 0 })
        {
            GeminiExpander.IsExpanded = true;
            JobState.Text = Loc.T("Det.NoApiKey");
            JobResult.Text = Loc.T("Det.AddApiKeyBody");
            Status(InfoBarSeverity.Warning, Loc.T("Det.AddApiKeyStatus"));
            return;
        }
        if (_meeting.MediaPath is null || !File.Exists(_meeting.MediaPath))
        {
            GeminiExpander.IsExpanded = true;
            JobState.Text = Loc.T("Det.NoRecording");
            JobResult.Text = Loc.T("Det.NoAudioBody");
            Status(InfoBarSeverity.Warning, Loc.T("Det.NoAudioStatus"));
            return;
        }

        SendButton.IsEnabled = false;
        GeminiExpander.IsExpanded = true;
        JobState.Text = Loc.T("Det.Uploading");
        JobResult.Text = "";
        try
        {
            using var transport = new GeminiHttpTransport(key);
            var uploader = new GeminiUploader(AppStore.Secrets, new LocalMeetingFileReader(), transport);
            var job = await uploader.SendMeetingAsync(_meeting, JsonSerializer.Serialize(Lines.ToList(), TranscriptJsonContext.Default.ListTranscriptLine), ResultDirectory);
            AppStore.Repo.SaveJob(job);

            JobState.Text = Loc.Tf("Det.LastResult", GeminiStateLabel(job.State), job.Attempts);
            if (job.State == GeminiJobState.Succeeded && job.ResultPath is not null)
            {
                JobResult.Text = File.ReadAllText(job.ResultPath);
                Status(InfoBarSeverity.Success, Loc.T("Det.GeminiSaved"));
            }
            else
            {
                JobResult.Text = job.LastError ?? Loc.T("Det.NoResult");
                Status(InfoBarSeverity.Error, Loc.Tf("Det.GeminiFailed", job.LastError ?? Loc.T("Det.UnknownReason")));
            }
        }
        catch (Exception ex)
        {
            JobResult.Text = Loc.T("Det.UploadFailed");
            Status(InfoBarSeverity.Error, Loc.Tf("Det.GeminiSendFailed", Short(ex)));
        }
        finally { SendButton.IsEnabled = true; }
    }

    private async void BackendSend_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting is null || _backendBusy) return;
        var url = (AppStore.Repo.GetSetting("backend.url") ?? "").Trim();
        var key = AppStore.Secrets.Get("BackendApiKey");
        BackendExpander.IsExpanded = true;
        if (url.Length == 0) { Status(InfoBarSeverity.Warning, Loc.T("Det.BackendNoUrl")); return; }
        if (key is not { Length: > 0 }) { Status(InfoBarSeverity.Warning, Loc.T("Det.BackendNoKey")); return; }
        if (_meeting.MediaPath is null || !File.Exists(_meeting.MediaPath))
        {
            BackendState.Text = Loc.T("Det.BackendNoRecording");
            Status(InfoBarSeverity.Warning, Loc.T("Det.BackendNoRecording"));
            return;
        }
        _backendBusy = true;
        BackendSendButton.IsEnabled = false;
        _backendCts?.Dispose();
        _backendCts = new CancellationTokenSource();
        _backendActiveJobId = null; // a previous run's job id must never be cancelled (review 3)
        var ct = _backendCts.Token;
        var meetingId = _meeting.Id;
        BackendSyncRow? existing = null;
        try
        {
            FlushEdits(); // pending debounce must reach the repo before we serialize the transcript
            var meeting = AppStore.Repo.Load(meetingId);
            if (meeting is null) { Status(InfoBarSeverity.Warning, Loc.T("Det.BackendNoMeeting")); return; }
            var segments = AppStore.Repo.LoadSegments(meetingId);
            var speakers = AppStore.Repo.LoadSpeakers(meetingId);
            var screens = AppStore.Repo.ListMediaTracks(meetingId)
                .Where(t => t.Kind == "screen" && File.Exists(t.Path)).ToList();
            existing = AppStore.Repo.LoadBackendSync(meetingId);
            _backendId = existing?.BackendId;

            AppStore.Repo.SaveBackendSync(new(meetingId, _backendId, "sending", null, existing?.Mock ?? false, NowIso()));
            BackendState.Text = Loc.T("Det.BackendSending");
            BackendSteps.Text = "";
            BackendProgress.IsIndeterminate = true;
            BackendProgress.Visibility = Visibility.Visible;

            var progress = new Progress<BackendSyncStage>(stage => BackendSteps.Text = Loc.Tf("Det.BackendSendingStep", StageLabel(stage)));
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            var sync = new BackendSync(client);
            var result = await sync.SendAsync(meeting, segments, speakers, screens, existing?.BackendId, progress, ct);
            _backendId = result.BackendId;
            AppStore.Repo.SaveBackendSync(new(meetingId, result.BackendId, "processing", null, existing?.Mock ?? false, NowIso()));
            BackendCancelButton.Visibility = Visibility.Visible;
            var final = await sync.WaitForTerminalAsync(result.BackendId, status =>
            {
                _backendActiveJobId = status.ActiveJobId;
                BackendProgress.IsIndeterminate = false;
                BackendProgress.Value = BackendPresentation.Percent(status.Progress);
                BackendState.Text = Loc.T(BackendPresentation.StatusKey(status.Status))
                    + (status.Mock ? " · " + Loc.T("Back.MockBadge") : "");
                BackendSteps.Text = BackendPresentation.StepsSummary(status.Steps);
            }, delay: null, ct);
            await SaveFinalBackendStateAsync(meeting, client, final, ct);
        }
        catch (OperationCanceledException ex)
        {
            // A user/unload cancel surfaces as an OCE whose token is ours; the HttpClient timeout
            // surfaces as an OCE with an inner TimeoutException. Both are terminal and must persist
            // a row - a cancelled send must not leave 'processing' in backend_sync forever (review 3).
            var cancelled = ex.InnerException is not TimeoutException && ex.CancellationToken == ct;
            var reason = cancelled ? Loc.T("Det.BackendCancelled") : Loc.T("Det.BackendTimeout");
            AppStore.Repo.SaveBackendSync(new(meetingId, _backendId, "failed", reason, existing?.Mock ?? false, NowIso()));
            BackendState.Text = reason;
            BackendSteps.Text = "";
        }
        catch (Exception ex)
        {
            var message = DescribeBackendError(ex);
            AppStore.Repo.SaveBackendSync(new(meetingId, _backendId, "failed", message, existing?.Mock ?? false, NowIso()));
            BackendState.Text = Loc.Tf("Det.BackendFailed", message);
            BackendSteps.Text = "";
            Status(InfoBarSeverity.Error, Loc.Tf("Det.BackendFailed", message));
        }
        finally
        {
            _backendBusy = false;
            BackendSendButton.IsEnabled = true;
            BackendCancelButton.Visibility = Visibility.Collapsed;
            BackendProgress.Visibility = Visibility.Collapsed;
        }
    }

    // Terminal status: cache the transcript + analysis so the Backend page opens offline-fast.
    private async Task SaveFinalBackendStateAsync(Meeting meeting, BackendClient client, BackendStatus final, CancellationToken ct)
    {
        if (final.Status != "ready")
        {
            AppStore.Repo.SaveBackendSync(new(meeting.Id, _backendId, final.Status, null, final.Mock, NowIso()));
            BackendSteps.Text = "";
            return;
        }
        try
        {
            var cache = new BackendCache(BackendCache.DefaultRoot);
            cache.SaveTranscript(meeting.Id, await client.GetTranscriptRawAsync(_backendId!, ct));
            cache.SaveAnalysis(meeting.Id, await client.GetAnalysisRawAsync(_backendId!, ct));
        }
        catch (Exception ex) { MeetVault.Core.AppLog.Warn("backend: cache write failed: " + ex.Message); }
        AppStore.Repo.SaveBackendSync(new(meeting.Id, _backendId, "ready", null, final.Mock, NowIso()));
        BackendState.Text = Loc.T("Det.BackendReady") + (final.Mock ? " · " + Loc.T("Back.MockBadge") : "");
        Status(InfoBarSeverity.Success, Loc.T("Det.BackendReady"));
    }

    private async void BackendCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting is null || !_backendBusy) return;
        // The job id only exists once the server reports a RUNNING job; before that (or between
        // chain steps) there is nothing to cancel yet - say so and keep polling (review 3: the old
        // code cancelled the local poll while the server ran the chain to completion).
        if (_backendActiveJobId is not { Length: > 0 })
        {
            Status(InfoBarSeverity.Informational, Loc.T("Det.BackendCancelPending"));
            return;
        }
        var url = (AppStore.Repo.GetSetting("backend.url") ?? "").Trim();
        var key = AppStore.Secrets.Get("BackendApiKey");
        if (url.Length == 0 || key is not { Length: > 0 }) { Status(InfoBarSeverity.Warning, Loc.T("Det.BackendNoUrl")); return; }
        try
        {
            using var transport = new BackendHttpTransport(url, key);
            var client = new BackendClient(transport);
            await client.CancelJobAsync(_backendActiveJobId);
            Status(InfoBarSeverity.Informational, Loc.T("Det.BackendCancelSent"));
            _backendCts?.Cancel(); // stop polling only after the server accepted the cancel
        }
        catch (Exception ex) { Status(InfoBarSeverity.Warning, Loc.Tf("Det.BackendFailed", DescribeBackendError(ex))); }
    }

    private void BackendOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting is null) return;
        PendingBackend.MeetingId = _meeting.Id;
        if (App.Window is MainWindow window) window.NavigateToBackend();
    }

    private bool TryTranscript(out string text)
    {
        text = string.Join(Environment.NewLine, Lines.Select(l => l.Speaker.Length == 0 ? l.Text : $"{l.Speaker}: {l.Text}"));
        if (Lines.Count > 0) return true;
        Status(InfoBarSeverity.Warning, Loc.T("Det.EmptyTranscript"));
        return false;
    }

    private static void ToClipboard(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static string ResultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetVault", "gemini");

    private static string FileNameFor(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var slug = new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && !invalid.Contains(c) ? c : '-').ToArray()).Trim('-');
        return (slug.Length == 0 ? "meeting" : slug) + ".transcript.txt";
    }

    private void Status(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    // One plain sentence for the user; the exception type stays out of the UI.
    private static string Short(Exception ex) => ex switch
    {
        UnauthorizedAccessException => Loc.T("Det.ErrPermissionDenied"),
        IOException => Loc.T("Det.ErrFileInUse"),
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 8 } => Loc.T("Det.ErrReadOnly"),
        _ => Loc.T("Det.ErrGeneric"),
    };
}

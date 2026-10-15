using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
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
    // Rate limit for the video drift correction (see SyncVideoTo): a decoder that cannot keep up
    // must not turn into a seek storm, because every seek restarts the decode pipeline.
    private long _lastVideoResyncTicks;
    // --- Analyze section ---
    private bool _backendBusy;
    private CancellationTokenSource? _backendCts;
    private string? _backendId;
    private string? _backendActiveJobId;
    // --- Analysis result (badge source): the LAST loaded analysis bundle ---
    private BackendBundle? _lastAnalysis;
    private string _stateLabelKey = "An.NotAnalyzed";
    // The server has no such meeting (the cloud copy was deleted): the link is dangling, the
    // state shows "Not on backend", and the action sends the meeting again from scratch.
    private bool _backendGone;
    // A cloud-only meeting (no local row) carries its backend id here; local meetings link
    // through _backendId / the backend_sync row.
    private string? _cloudBackendId = null;
    // --- Player transport ---
    private bool _draggingSlider;
    private bool _applyingUi;
    // --- Responsive Details layout: two-column workspace vs stacked ---
    // MUST start Stacked: the T4 XAML is authored in the stacked arrangement, and the
    // first transition only fires when the real content size says two-column.
    private DetailsLayoutMode _layoutMode = DetailsLayoutMode.Stacked;
    // --- Cloud transcript + summary ---
    private string _summaryPlain = "";
    private readonly List<BackendSegment> _cloudSegments = new();

    public DetailsPage()
    {
        InitializeComponent();
        HookTransportInput();

        // Release the players' sources on unload for every path (local, cloud-only, no meeting):
        // registering this before the cloud-mode early return keeps cloud mode covered too (review fix).
        Unloaded += (_, _) =>
        {
            try { Player.Source = null; } catch { }
            try { VideoPlayer.Source = null; } catch { }
            try { _backendCts?.Cancel(); } catch { } // the poll stops with the page (plan decision #8)
        };

        var cloudBackendId = PendingDetails.CloudBackendId;
        PendingDetails.CloudBackendId = null;
        if (cloudBackendId is not null)
        {
            _cloudBackendId = cloudBackendId;
            _meeting = null;
            // A pending local handoff must not survive a cloud navigation (spec D11 prose).
            PendingDetails.MeetingId = null;
            InitializeCloudMode();
            return;
        }

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
            LocalTranscriptList.Visibility = Visibility.Collapsed;
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
        LocalTranscriptList.ItemsSource = Lines.ToArray();
        TranscriptSource.Text = Loc.T("Det.SourceLocal");
        ShowPlayer();
        ShowVideo();
        SubscribePlayhead();
        _ = RefreshAnalyzeAsync();
        _ = LoadLocalCloudContentAsync();
        VideoPlayer.SizeChanged += (_, _) => ApplyAspect();
        Player.SizeChanged += (_, _) => ApplyAspect();
        ApplyAspect();
        UpdateLineStatus();
        EvaluateLayout();
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
        session.NaturalDurationChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTransportUi);
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
                    DispatcherQueue.TryEnqueue(UpdateTransportUi);
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
        => DispatcherQueue.TryEnqueue(() =>
        {
            SyncVideoTo(sender.Position);
            UpdateTransportUi();
        });

    // The sound is the master: the video plays while the audio plays, holds its frame otherwise.
    // This route alone is not enough (and used to be outright wrong): at the instant Play is
    // pressed the playhead has not yet reached the run's start offset, so the old
    // `position >= StartOffsetMs` sample read "before the run" and PAUSED the video for the whole
    // run. The drift seek below then became the only thing that moved it - one ~500 ms jump every
    // ~half second, i.e. an apparent ~2 fps for a 30 fps file (measured 2026-10-15 on a 64 s
    // track: video state=Paused at every tick). So the play state is reconciled here AND on every
    // position tick, in SyncVideoTo, which is idempotent.
    private void OnAudioPlaybackStateChanged(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        => DispatcherQueue.TryEnqueue(() => SyncVideoTo(sender.Position));

    // The drift beyond which the video is pulled back onto the audio playhead. Every resync is a
    // seek, and a seek restarts the decode pipeline (a visible stall), so the correction is rate
    // limited by ResyncAllowed and never used as the mechanism that advances the picture.
    private const double DriftToleranceMs = 400;
    private const double ResyncCooldownSeconds = 2;

    // Keeps the video within DriftToleranceMs of the audio playhead (shifted by the active run's
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
            var audioPlaying = Player.MediaPlayer?.PlaybackSession?.PlaybackState
                == Windows.Media.Playback.MediaPlaybackState.Playing;
            var playing = videoSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
            try
            {
                if (target < 0)
                {
                    // Before this run starts: hold its first frame.
                    if (playing) vmp.Pause();
                    if (videoSession.Position != TimeSpan.Zero) videoSession.Position = TimeSpan.Zero;
                }
                else if (audioPlaying)
                {
                    if (!playing)
                    {
                        // Start aligned: seek to the playhead FIRST, then play. Playing from 0 here
                        // would leave the picture a fixed offset behind the sound for the whole run.
                        videoSession.Position = TimeSpan.FromMilliseconds(target);
                        vmp.Play();
                        MeetVault.Core.AppLog.Info("details: video play at " + (long)target
                            + " ms (run offset " + _videoTrack.StartOffsetMs + " ms)");
                    }
                    else if (Math.Abs(videoSession.Position.TotalMilliseconds - target) > DriftToleranceMs && ResyncAllowed())
                    {
                        var drift = (long)(videoSession.Position.TotalMilliseconds - target);
                        videoSession.Position = TimeSpan.FromMilliseconds(target);
                        MeetVault.Core.AppLog.Info("details: video drift " + drift + " ms -> resynced to " + (long)target + " ms");
                    }
                }
                else
                {
                    // Paused (or ended): follow the playhead without playing, so a paused seek shows
                    // the right frame instead of the frame it happened to stop on.
                    if (playing) vmp.Pause();
                    if (Math.Abs(videoSession.Position.TotalMilliseconds - target) > 40)
                        videoSession.Position = TimeSpan.FromMilliseconds(target);
                }
            }
            catch { }
        }
        HighlightSegment(ms);
    }

    private bool ResyncAllowed()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastVideoResyncTicks != 0 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_lastVideoResyncTicks).TotalSeconds < ResyncCooldownSeconds) return false;
        _lastVideoResyncTicks = now;
        return true;
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
    }

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
        var path = Path.Combine(folder, SlugFor(_meeting?.Title ?? "meeting") + ".transcript.txt");
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

    // --- Player control bar (D6): play/pause, seek, volume, fullscreen ---
    // Fixed 16:9 (owner direction 2026-10-14): the aspect-cycle button is gone; the
    // element height is computed from its own width so the ratio holds at any window
    // size; Stretch=Uniform keeps the video uncropped.

    // Responsive Details layout. The mode follows the CONTENT size (the RootScroll
    // viewport: the page's own width, the navigation pane already excluded), decided by
    // the pure Core helper so the rule is unit-testable. Re-evaluated on every resize;
    // the load paths re-run it after each meeting load as belt-and-braces.
    // Wide (the big-screen workspace, owner direction 2026-10-15: a smaller screen must
    // keep this same UI) is used at EVERY content size its two regions fit in (see
    // DetailsLayout.MinWideWidthDip): exactly two sections - the 16:9 video section left and
    // WIDEST; a header row (title + StatusBar) spans both columns; the rail's panels
    // ALIGN with the meeting panel; Analyze + Summary cover the top of the rail and the
    // TRANSCRIPT section covers the remaining part as its own bounded scrollable pane;
    // the grid fills the viewport width (no 1200 cap) and the video grows with its
    // section.
    private void RootScroll_SizeChanged(object sender, SizeChangedEventArgs e) => EvaluateLayout();

    private void EvaluateLayout()
    {
        if (RootScroll.ActualWidth <= 0 || RootScroll.ActualHeight <= 0) return; // not laid out yet
        var mode = DetailsLayout.Decide(RootScroll.ActualWidth, RootScroll.ActualHeight);
        if (mode != _layoutMode) ApplyLayout(mode);
        else if (mode != DetailsLayoutMode.Stacked) PageGrid.Height = RootScroll.ActualHeight;
        ApplyAspect(); // the video height follows the mode's cap and its section width
    }

    private static void DetachFromParent(FrameworkElement element)
    {
        if (element.Parent is Panel panel) panel.Children.Remove(element);
    }

    // In the wide tier the rail grid is pinned to the rail viewport (a star row under a
    // ScrollViewer would otherwise size to its content, leaving the transcript
    // unbounded); the belt pin in ApplyLayout is corrected here to the exact cell.
    private void RightRegion_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layoutMode == DetailsLayoutMode.Wide) RightGrid.Height = RightRegion.ActualHeight;
    }

    private void ApplyLayout(DetailsLayoutMode mode)
    {
        _layoutMode = mode;
        DetachFromParent(TranscriptCard);
        DetachFromParent(AnalyzeCard);
        DetachFromParent(SummaryCard);
        DetachFromParent(RightRegion);
        DetachFromParent(TitleBlock);
        DetachFromParent(StatusBar);
        PageGrid.ColumnDefinitions.Clear();
        PageGrid.RowDefinitions.Clear();
        if (mode == DetailsLayoutMode.Wide)
        {
            // Owner direction (2026-10-14, round 3): exactly two sections. A header row
            // (title + StatusBar) spans both columns; row 1 holds the media section and
            // the rail, their tops level with the MEETING card. The rail covers Analyze
            // + Summary at its top and the TRANSCRIPT section covers the remaining
            // part as its own bounded scrollable pane. The player card never moves —
            // media playback is never disrupted by a mode switch.
            PageGrid.MaxWidth = double.PositiveInfinity;
            PageGrid.Height = RootScroll.ActualHeight;
            PageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = DetailsLayout.MinMediaWidthDip });
            PageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = DetailsLayout.MinRailWidthDip, MaxWidth = 640 });
            PageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            // HeaderStack is a PERMANENT PageGrid child (XAML) - position it, never
            // re-add it: UIElementCollection.Add on an already-parented element
            // throws and kills the process (execution-proven 2026-10-15 00:08).
            Grid.SetRow(HeaderStack, 0);
            Grid.SetColumn(HeaderStack, 0);
            Grid.SetColumnSpan(HeaderStack, 2);
            HeaderStack.Visibility = Visibility.Visible;
            HeaderStack.Children.Add(TitleBlock);
            HeaderStack.Children.Add(StatusBar);
            Grid.SetRow(LeftRegion, 1);
            Grid.SetColumn(LeftRegion, 0);
            PageGrid.Children.Add(RightRegion);
            Grid.SetRow(RightRegion, 1);
            Grid.SetColumn(RightRegion, 1);
            RightRegion.Visibility = Visibility.Visible;
            RightGrid.Height = RootScroll.ActualHeight; // belt; RightRegion_SizeChanged pins the exact cell
            RightGrid.Children.Add(AnalyzeCard);
            Grid.SetRow(AnalyzeCard, 0);
            Grid.SetColumn(AnalyzeCard, 0);
            RightGrid.Children.Add(SummaryCard);
            Grid.SetRow(SummaryCard, 1);
            Grid.SetColumn(SummaryCard, 0);
            RightGrid.Children.Add(TranscriptCard);
            Grid.SetRow(TranscriptCard, 2);
            Grid.SetColumn(TranscriptCard, 0);
        }
        else
        {
            // Natural height: LeftRegion and the card's TranscriptScroll grow to their
            // content, so the OUTER RootScroll scrolls the whole page in the stacked
            // order: title, StatusBar, meeting, player, TRANSCRIPT, analyze, summary.
            PageGrid.MaxWidth = 1200;
            PageGrid.Height = double.NaN;
            HeaderStack.Visibility = Visibility.Collapsed;
            Grid.SetRow(LeftRegion, 0);
            Grid.SetColumn(LeftRegion, 0);
            RightRegion.Visibility = Visibility.Collapsed;
            LeftStack.Children.Insert(0, TitleBlock);
            LeftStack.Children.Insert(1, StatusBar);
            LeftStack.Children.Add(TranscriptCard);
            Grid.SetColumn(TranscriptCard, 0);
            LeftStack.Children.Add(AnalyzeCard);
            LeftStack.Children.Add(SummaryCard);
            PageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
    }

    private void ApplyAspect()
    {
        var target = VideoPlayer.Visibility == Visibility.Visible ? (FrameworkElement)VideoPlayer : Player;
        var naturalW = VideoPlayer.MediaPlayer?.PlaybackSession?.NaturalVideoWidth ?? 0;
        var naturalH = VideoPlayer.MediaPlayer?.PlaybackSession?.NaturalVideoHeight ?? 0;
        if (target.ActualWidth <= 0) return; // not laid out yet; SizeChanged re-runs this
        // In the wide tier the video section is the widest section, so the 16:9 video
        // grows with it, up to the viewport minus the header row and the section's card
        // chrome; in every other tier the historical 540 cap holds.
        var maxHeight = _layoutMode == DetailsLayoutMode.Wide
            ? Math.Max(540, RootScroll.ActualHeight - 480)
            : 540;
        target.Height = PlayerGeometry.HeightFor(target.ActualWidth, PlayerAspect.Ratio16x9, maxHeight, naturalW, naturalH)
                        ?? double.NaN;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var target = VideoPlayer.Visibility == Visibility.Visible ? VideoPlayer : Player;
        target.IsFullWindow = !target.IsFullWindow;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;
        if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing) Player.MediaPlayer!.Pause();
        else Player.MediaPlayer!.Play();
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_applyingUi) return; // a programmatic slider update must not re-apply the volume
        ApplyVolume(e.NewValue / 100.0);
    }

    private void ApplyVolume(double volume)
    {
        _applyingUi = true;
        try
        {
            if (Player.MediaPlayer is { } mp) mp.Volume = Math.Clamp(volume, 0, 1);
            if (VideoPlayer.MediaPlayer is { } vmp) vmp.IsMuted = true; // video follows the audio master
        }
        finally { _applyingUi = false; }
    }

    // A Slider handles the pointer ITSELF: a thumb drag and a track press both mark the routed
    // event handled, so a XAML PointerPressed/PointerReleased attribute on the Slider never fires
    // (proven live 2026-10-15: a real press-drag-release moved the slider's value while the
    // playhead stayed exactly where it was - the slider looked dead). Hook the events with
    // handledEventsToo so the drag state is reliable, and let ValueChanged carry the seek for
    // every other user gesture (a track click, arrow keys, an assistive-technology range set).
    private void HookTransportInput()
    {
        PositionSlider.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _draggingSlider = true), true);
        PositionSlider.AddHandler(Microsoft.UI.Xaml.UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSliderPointerReleased), true);
        PositionSlider.AddHandler(Microsoft.UI.Xaml.UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSliderPointerReleased), true);
    }

    private void OnSliderPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_draggingSlider) return; // a capture loss with no press: nothing to commit
        _draggingSlider = false;
        CommitSliderSeek();
    }

    private void PositionSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_applyingUi) return;     // the playhead writes the slider every tick: never a seek
        if (_draggingSlider) return; // a drag commits once, on release
        CommitSliderSeek();
    }

    // Slider position -> master (audio) playhead, in the meeting's own timeline. The labels are
    // written from the REQUESTED position: MediaPlayer applies a seek asynchronously, so reading
    // session.Position straight back would restore the pre-seek value and look like a dead slider.
    private void CommitSliderSeek()
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;
        var totalMs = session.NaturalDuration.TotalMilliseconds;
        if (totalMs <= 0) return; // no duration yet (nothing loaded, or a cloud track still arriving)
        var targetMs = Math.Clamp(PositionSlider.Value / 1000.0 * totalMs, 0, totalMs);
        try { session.Position = TimeSpan.FromMilliseconds(targetMs); } catch { }
        var target = TimeSpan.FromMilliseconds(targetMs);
        PositionText.Text = FormatClock(target);
        SyncVideoTo(target); // the video follows at once (a paused seek raises no position tick)
    }

    // Transport UI: position text + slider + duration; the slider never fights an active drag.
    private void UpdateTransportUi()
    {
        var session = Player.MediaPlayer?.PlaybackSession;
        if (session is null) return;
        if (!_draggingSlider)
        {
            _applyingUi = true;
            try
            {
                PositionSlider.Maximum = 1000;
                PositionSlider.Value = session.NaturalDuration.TotalMilliseconds > 0
                    ? Math.Clamp(session.Position.TotalMilliseconds / session.NaturalDuration.TotalMilliseconds * 1000, 0, 1000)
                    : 0;
            }
            finally { _applyingUi = false; }
        }
        PositionText.Text = FormatClock(session.Position);
        DurationText.Text = FormatClock(session.NaturalDuration);
        PlayPauseButton.Content = session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing ? "\u275A\u275A" : "\u25B6";
    }

    private static string FormatClock(TimeSpan span)
    {
        var total = (int)Math.Max(0, span.TotalSeconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    // --- Analyze section (D7): 6-state workflow over the backend status ---

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting is null && _cloudBackendId is null) return;
        if (_backendBusy) { Status(InfoBarSeverity.Informational, Loc.T("DetAnalyzeRunning")); return; }
        if (!AppSession.TryCreateClient(out var client))
        {
            Status(InfoBarSeverity.Warning, AppSession.LastError ?? Loc.T("DetAnalyzeSignIn"));
            RenderAnalyzeState(AnalysisState.NotAnalyzed, null);
            return;
        }
        _backendBusy = true;
        AnalyzeButton.IsEnabled = false;
        AnalyzeCancelButton.IsEnabled = true;
        _backendCts?.Dispose();
        _backendCts = new CancellationTokenSource();
        _backendActiveJobId = null;
        _lastAnalysis = null; // a new run: the old bundle's mock flag no longer applies
        UpdateMockBadge();
        var ct = _backendCts.Token;
        try
        {
            var meetingId = _meeting?.Id;
            if (meetingId is not null) FlushEdits();
            var existing = meetingId is null ? null : AppStore.Repo.LoadBackendSync(meetingId);
            var backendId = _cloudBackendId ?? (_backendGone ? null : existing?.BackendId);
            _backendId = backendId;
            RenderAnalyzeState(AnalysisState.Uploading, null);
            if (backendId is not null)
            {
                // Already uploaded (or cloud-only): a re-run forces the analysis again. A 404 here
                // means the cloud copy was deleted, so the link is dropped and the send below
                // re-creates the meeting (the server's create is idempotent on source+external_id,
                // and it replaces the tombstone the delete left behind).
                try
                {
                    var rerun = await client.ProcessAsync(backendId, new[] { "analyze" }, force: true, ct: ct);
                    if (rerun.JobIds.Count > 0) _backendActiveJobId = rerun.JobIds[0];
                }
                catch (BackendApiException ex) when (ex.Code == "not_found")
                {
                    MeetVault.Core.AppLog.Warn("backend: cloud copy is gone (" + backendId + "); re-sending the meeting");
                    if (meetingId is not null) AppStore.Repo.ClearBackendSync(meetingId);
                    _backendId = null;
                    backendId = null;
                    _backendGone = false;
                }
            }
            if (backendId is null)
            {
                var meeting = AppStore.Repo.Load(meetingId!);
                if (meeting is null) { Status(InfoBarSeverity.Warning, Loc.T("Det.BackendNoMeeting")); return; }
                var segments = AppStore.Repo.LoadSegments(meeting.Id);
                var speakers = AppStore.Repo.LoadSpeakers(meeting.Id);
                var screens = AppStore.Repo.ListMediaTracks(meeting.Id)
                    .Where(t => t.Kind == "screen" && File.Exists(t.Path)).ToList();
                var progress = new Progress<BackendSyncStage>(stage =>
                    AnalyzeStepsText.Text = Loc.Tf("Det.BackendSendingStep", StageLabel(stage)));
                var result = await new BackendSync(client).SendAsync(meeting, segments, speakers, screens, null, progress, ct);
                backendId = result.BackendId;
                _backendId = backendId;
                if (result.Process.JobIds.Count > 0) _backendActiveJobId = result.Process.JobIds[0];
                if (meetingId is not null)
                    AppStore.Repo.SaveBackendSync(new(meetingId, backendId, "processing", null, false, NowIso()));
            }
            else if (meetingId is not null)
            {
                AppStore.Repo.SaveBackendSync(new(meetingId, backendId, "sending", null, existing?.Mock ?? false, NowIso()));
            }
            RenderAnalyzeState(AnalysisState.Queued, null);
            var final = await new BackendSync(client).WaitForTerminalAsync(backendId, status =>
            {
                if (!string.IsNullOrEmpty(status.ActiveJobId)) _backendActiveJobId = status.ActiveJobId;
                AnalyzeProgress.IsIndeterminate = false;
                AnalyzeProgress.Value = BackendPresentation.Percent(status.Progress);
                AnalyzeStepsText.Text = BackendPresentation.StepsSummary(status.Steps);
            }, delay: null, ct);
            await OnAnalysisTerminalAsync(client, backendId, meetingId, final, ct);
        }
        catch (OperationCanceledException ex)
        {
            var cancelled = ex.InnerException is not TimeoutException && ex.CancellationToken == ct;
            PersistSyncFailure(meetingId: _meeting?.Id, cancelled ? Loc.T("Det.BackendCancelled") : Loc.T("Det.BackendTimeout"));
        }
        catch (BackendApiException ex)
        {
            if (ex.StatusCode == 401) AppSession.NoteAuthFailure(ex);
            PersistSyncFailure(meetingId: _meeting?.Id, DescribeBackendError(ex));
        }
        catch (Exception ex)
        {
            PersistSyncFailure(meetingId: _meeting?.Id, DescribeBackendError(ex));
        }
        finally
        {
            _backendBusy = false;
            _backendActiveJobId = null;
            AnalyzeButton.IsEnabled = true;
            AnalyzeCancelButton.IsEnabled = false;
            await RefreshAnalyzeAsync();
        }
    }

    private void PersistSyncFailure(string? meetingId, string message)
    {
        if (meetingId is not null)
            AppStore.Repo.SaveBackendSync(new(meetingId, _backendId, "failed", message, false, NowIso()));
        Status(InfoBarSeverity.Error, Loc.Tf("DetAnalyzeFailed", message));
    }

    private async Task OnAnalysisTerminalAsync(BackendClient client, string backendId, string? meetingId, BackendStatus final, CancellationToken ct)
    {
        if (meetingId is not null)
            AppStore.Repo.SaveBackendSync(new(meetingId, backendId, final.Status, final.Status == "ready" ? null : final.Error, final.Mock, NowIso()));
        if (final.Status == "ready")
        {
            try
            {
                var cache = new BackendCache(BackendCache.DefaultRoot);
                cache.SaveTranscript(meetingId ?? backendId, await client.GetTranscriptRawAsync(backendId, ct));
                cache.SaveAnalysis(meetingId ?? backendId, await client.GetAnalysisRawAsync(backendId, ct));
            }
            catch (Exception ex) { MeetVault.Core.AppLog.Warn("backend: cache write failed: " + ex.Message); }
            Status(InfoBarSeverity.Success, Loc.T("DetAnalyzeSubmitted"));
            await LoadCloudContentAsync(client, backendId, ct); // swaps transcript + summary in
        }
    }

    // State render + refresh: backend status is authoritative whenever reachable.
    private async Task RefreshAnalyzeAsync()
    {
        var backendId = _cloudBackendId ?? _backendId;
        if (backendId is null)
        {
            var sync = _meeting is null ? null : AppStore.Repo.LoadBackendSync(_meeting.Id);
            _backendId = sync?.BackendId;
            RenderAnalyzeState(BackendAnalysisState.Map(sync, null, false), null);
            return;
        }
        if (!AppSession.TryCreateClient(out var client))
        {
            RenderAnalyzeState(BackendAnalysisState.Map(AppStore.Repo.LoadBackendSync(_meeting?.Id ?? ""), null, false), null);
            return;
        }
        try
        {
            var status = await client.GetStatusAsync(backendId);
            var hasAnalysis = status.FreshKinds.Count > 0;
            _backendGone = false;
            RenderAnalyzeState(BackendAnalysisState.Map(AppStore.Repo.LoadBackendSync(_meeting?.Id ?? ""), status, hasAnalysis), status);
        }
        catch (BackendApiException ex) when (ex.Code == "not_found" && _meeting is not null)
        {
            // The server deleted this meeting: record the dangling link, drop the cached id so the
            // next Analyze re-sends instead of re-running against a dead id (BackendAction.Send),
            // and show the "Not on backend" state rather than an analysis failure.
            AppStore.Repo.SaveBackendSync(new(_meeting.Id, backendId, "not_found", "not_found: the cloud copy is gone", false, NowIso()));
            _backendId = null;
            _backendGone = true;
            _lastAnalysis = null;
            UpdateMockBadge();
            RenderAnalyzeState(AnalysisState.NotAnalyzed, null);
        }
        catch (BackendApiException ex)
        {
            if (ex.StatusCode == 401) AppSession.NoteAuthFailure(ex);
            RenderAnalyzeState(AnalysisState.NotAnalyzed, null);
        }
        catch
        {
            var sync = _meeting is null ? null : AppStore.Repo.LoadBackendSync(_meeting.Id);
            RenderAnalyzeState(BackendAnalysisState.Map(sync, null, false), null);
        }
    }

    private void RenderAnalyzeState(AnalysisState state, BackendStatus? status)
    {
        var labelKey = state switch
        {
            AnalysisState.Uploading => "An.Uploading",
            AnalysisState.Queued => "An.Queued",
            AnalysisState.Processing => "An.Processing",
            AnalysisState.Completed => "An.Completed",
            AnalysisState.Failed => "An.Failed",
            _ => "An.NotAnalyzed",
        };
        _stateLabelKey = labelKey;
        AnalyzeStateText.Text = BuildAnalyzeStateText();
        AnalyzeButton.Visibility = state is AnalysisState.Uploading or AnalysisState.Queued or AnalysisState.Processing
            ? Visibility.Collapsed : Visibility.Visible;
        AnalyzeButton.Content = state switch
        {
            AnalysisState.Completed => Loc.T("DetAnalyzeAgain.Content"),
            AnalysisState.Failed => Loc.T("DetAnalyzeRetry.Content"),
            // The meeting is not on the backend (yet, or any more): the action is a send, not a retry.
            _ when _backendGone => Loc.T("DetAnalyzeSendAgain.Content"),
            _ => Loc.T("DetAnalyze.Content"),
        };
        var forget = state == AnalysisState.Failed
            && _meeting is not null
            && AppStore.Repo.LoadBackendSync(_meeting.Id)?.State == "not_found";
        AnalyzeForgetButton.Visibility = forget ? Visibility.Visible : Visibility.Collapsed;
        var canSignIn = !AppSession.IsSignedIn && state == AnalysisState.NotAnalyzed && _meeting?.MediaPath is not null;
        AnalyzeSignInButton.Visibility = canSignIn ? Visibility.Visible : Visibility.Collapsed;
        AnalyzeCancelButton.Visibility = state is AnalysisState.Uploading or AnalysisState.Queued or AnalysisState.Processing
            ? Visibility.Visible : Visibility.Collapsed;
        AnalyzeProgress.Visibility = state is AnalysisState.Uploading or AnalysisState.Queued or AnalysisState.Processing
            ? Visibility.Visible : Visibility.Collapsed;
        if (_backendGone && state == AnalysisState.NotAnalyzed)
        {
            // Say what is actually true and what the button will do, instead of the bare
            // "Not analyzed" that used to sit beside a red not_found error bar.
            var reason = AppStore.Repo.LoadBackendSync(_meeting?.Id ?? "")?.State == "not_found"
                ? Loc.T("An.NotOnBackendGone")
                : Loc.T("An.NotOnBackend");
            AnalyzeStateText.Text = Loc.Tf("DetAnalyzeStatusLine", Loc.T("An.NotOnBackend"), reason);
        }
        if (state == AnalysisState.NotAnalyzed && _meeting is { MediaPath: null } && _cloudBackendId is null)
        {
            AnalyzeStateText.Text = Loc.T("DetAnalyzeNoRecording");
            AnalyzeButton.Visibility = Visibility.Collapsed;
        }
    }

    // The mock badge reflects the ANALYSIS result (the Core mapping), never the pipeline
    // status flag: a real analyzer run over a mock server-side transcript is a real analysis.
    private void UpdateMockBadge()
    {
        AnalyzeMockBadge.Text = Loc.T("Back.MockBadge");
        AnalyzeMockBadge.Visibility = BackendAnalysisState.ShowMockBadge(_lastAnalysis)
            ? Visibility.Visible : Visibility.Collapsed;
        AnalyzeStateText.Text = BuildAnalyzeStateText();
    }

    private string BuildAnalyzeStateText()
        => Loc.T(_stateLabelKey)
            + (BackendAnalysisState.ShowMockBadge(_lastAnalysis) ? " \u00b7 " + Loc.T("Back.MockBadge") : "");

    private void AnalyzeCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_backendActiveJobId is not { Length: > 0 }) { Status(InfoBarSeverity.Informational, Loc.T("Det.BackendCancelPending")); return; }
        if (!AppSession.TryCreateClient(out var client)) return;
        _ = CancelJobAsync(client);
    }

    private async Task CancelJobAsync(BackendClient client)
    {
        try { await client.CancelJobAsync(_backendActiveJobId!); Status(InfoBarSeverity.Informational, Loc.T("Det.BackendCancelSent")); _backendCts?.Cancel(); }
        catch (Exception ex) { Status(InfoBarSeverity.Warning, Loc.Tf("DetAnalyzeFailed", DescribeBackendError(ex))); }
    }

    private void AnalyzeForget_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting is null) return;
        AppStore.Repo.ClearBackendSync(_meeting.Id);
        _backendId = null;
        _lastAnalysis = null;
        UpdateMockBadge();
        RenderAnalyzeState(AnalysisState.NotAnalyzed, null);
    }

    private void AnalyzeSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is MainWindow window) window.NavigateToAccount();
    }

    // --- Cloud mode (D11): a meeting opened from the Cloud Storage list with no local row ---

    private async void InitializeCloudMode()
    {
        MeetingTitle.Text = Loc.T("Det.LoadingCloudMeeting");
        if (!AppSession.TryCreateClient(out var client))
        {
            MeetingTitle.Text = Loc.T("DetAnalyzeSignIn");
            RenderAnalyzeState(AnalysisState.NotAnalyzed, null);
            return;
        }
        try
        {
            var detail = await client.GetMeetingAsync(_cloudBackendId!);
            MeetingTitle.Text = detail.Title;
            MeetingMeta.Text = (detail.StartedAt ?? detail.CreatedAt ?? "") + " \u00b7 " + detail.DurationMs / 1000 + "s";
            await RefreshAnalyzeAsync();
            await LoadCloudContentAsync(client, _cloudBackendId!);
            await LoadCloudAudioAsync(client);
            EvaluateLayout();
        }
        catch (BackendApiException ex)
        {
            if (ex.StatusCode == 401) AppSession.NoteAuthFailure(ex);
            Status(InfoBarSeverity.Error, Loc.Tf("Cloud.Error", DescribeBackendError(ex)));
        }
        catch (Exception ex)
        {
            Status(InfoBarSeverity.Error, Loc.Tf("Cloud.Error", DescribeBackendError(ex)));
        }
    }

    // Cloud-only meetings have no local recording: fetch the audio into the cache, then play it
    // through the same Player element (Range-resume capable via the transport).
    private async Task LoadCloudAudioAsync(BackendClient client)
    {
        try
        {
            var media = await client.ListMediaAsync(_cloudBackendId!);
            var audio = media.FirstOrDefault(m => m.Kind == "audio");
            if (audio is null) return;
            var extension = audio.OriginalName.Contains('.') ? audio.OriginalName[(audio.OriginalName.LastIndexOf('.') + 1)..] : "m4a";
            var path = new BackendCache(BackendCache.DefaultRoot).MediaPath(audio.Id, extension);
            if (!File.Exists(path)) await client.DownloadMediaAsync(audio.Id, path, default);
            Player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
            Player.Visibility = Visibility.Visible;
            SubscribePlayhead();
            ApplyAspect();
        }
        catch (Exception ex) { MeetVault.Core.AppLog.Warn("details: cloud audio failed: " + ex.Message); }
    }

    // Local meetings that already have a cloud copy: fetch transcript/summary for the swap.
    private async Task LoadLocalCloudContentAsync()
    {
        if (_meeting is null) return;
        var sync = AppStore.Repo.LoadBackendSync(_meeting.Id);
        if (sync?.BackendId is { } id && sync.State is "ready" or "processing")
        {
            if (AppSession.TryCreateClient(out var c)) await LoadCloudContentAsync(c, id);
        }
    }

    // --- Cloud transcript + summary (D8) ---

    // Fetch + render everything the cloud side has for this meeting (transcript, summary, Q&A).
    private async Task LoadCloudContentAsync(BackendClient client, string backendId, CancellationToken ct = default)
    {
        try
        {
            var transcript = await client.GetActiveTranscriptAsync(backendId, ct);
            RenderCloudTranscript(transcript);
        }
        catch (BackendApiException ex) when (ex.Code == "not_found")
        {
            ShowLocalTranscript(); // no server transcript yet: local stays on screen
        }
        catch (Exception) { ShowLocalTranscript(); }
        try
        {
            var bundle = await client.GetAnalysisAsync(backendId, ct);
            RenderSummary(bundle);
        }
        catch (Exception) { /* summary area keeps its during-state */ }
    }

    private void RenderCloudTranscript(BackendTranscriptDetail transcript)
    {
        _cloudSegments.Clear();
        var rows = new List<CloudLineRow>();
        foreach (var segment in transcript.Segments)
        {
            _cloudSegments.Add(segment);
            rows.Add(new CloudLineRow
            {
                Time = FormatClock(TimeSpan.FromMilliseconds(segment.StartMs)),
                Speaker = segment.SpeakerLabel,
                Text = segment.Text,
                StartMs = segment.StartMs,
            });
        }
        CloudTranscriptList.ItemsSource = null;
        CloudTranscriptList.ItemsSource = rows;
        TranscriptSource.Text = Loc.T("Det.SourceCloud");
        LocalTranscriptList.Visibility = Visibility.Collapsed;
        CloudTranscriptList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count == 0) EmptyState.Text = Loc.T("DetCloudEmpty");
    }

    private void ShowLocalTranscript()
    {
        TranscriptSource.Text = Loc.T("Det.SourceLocal");
        CloudTranscriptList.Visibility = Visibility.Collapsed;
        LocalTranscriptList.Visibility = Visibility.Visible;
        UpdateLineStatus();
    }

    private void RenderSummary(BackendBundle bundle)
    {
        SummaryContent.Visibility = Visibility.Visible;
        SummaryStateText.Text = "";
        SummaryOverviewText.Text = bundle.Summary?.Text ?? "";
        SummaryBullets.ItemsSource = bundle.Summary?.Bullets.ToArray() ?? Array.Empty<string>();
        AgendaList.ItemsSource = bundle.Agenda
            .Select(a => new DetailRow(
                a.Title,
                $"[{FormatClock(TimeSpan.FromMilliseconds(a.StartMs))}-{FormatClock(TimeSpan.FromMilliseconds(a.EndMs))}]  {a.Topic}"))
            .ToArray();
        DecisionsList.ItemsSource = bundle.Decisions
            .Select(d => new DetailRow(d.Text, d.DecidedBy.Count > 0 ? string.Join(", ", d.DecidedBy) : "")
            {
                StartMs = d.AtMs,
                SegmentIds = d.SegmentIds,
            })
            .ToArray();
        ActionsList.ItemsSource = bundle.Actions
            .Select(a => new DetailRow(
                a.Text,
                (a.Owner.Length > 0 ? "owner: " + a.Owner : "") + (a.Due is { Length: > 0 } due ? "  due: " + due : ""))
            {
                SegmentIds = a.SegmentIds,
            })
            .ToArray();
        QaList.ItemsSource = bundle.Qa
            .Select(q => new DetailRow(q.Question, q.Answer)
            {
                StartMs = q.Citations.Count > 0 ? q.Citations[0].StartMs : null,
            })
            .ToArray();
        _summaryPlain = BuildSummaryPlainText(bundle);
        _lastAnalysis = bundle;
        UpdateMockBadge();
    }

    // The exact text the Copy-summary button puts on the clipboard: overview, bullets,
    // agenda, decisions, action items, Q&A - sections with no content are skipped.
    private static string BuildSummaryPlainText(BackendBundle bundle)
    {
        var sb = new System.Text.StringBuilder();
        if (bundle.Summary?.Text is { Length: > 0 } overview) sb.AppendLine(overview);
        foreach (var bullet in bundle.Summary?.Bullets ?? new List<string>()) sb.AppendLine("- " + bullet);
        if (bundle.Agenda.Count > 0)
        {
            sb.AppendLine().AppendLine("Agenda & discussion");
            foreach (var item in bundle.Agenda) sb.AppendLine($"- {item.Title} [{FormatClock(TimeSpan.FromMilliseconds(item.StartMs))}-{FormatClock(TimeSpan.FromMilliseconds(item.EndMs))}] {item.Topic}");
        }
        if (bundle.Decisions.Count > 0)
        {
            sb.AppendLine().AppendLine("Decisions");
            foreach (var decision in bundle.Decisions) sb.AppendLine("- " + decision.Text);
        }
        if (bundle.Actions.Count > 0)
        {
            sb.AppendLine().AppendLine("Action items");
            foreach (var item in bundle.Actions) sb.AppendLine("- " + item.Text);
        }
        if (bundle.Qa.Count > 0)
        {
            sb.AppendLine().AppendLine("Q&A");
            foreach (var qa in bundle.Qa) sb.AppendLine(qa.Question + " -> " + qa.Answer);
        }
        return sb.ToString().TrimEnd();
    }

    private void CloudRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not CloudLineRow row) return;
        SeekMaster(row.StartMs);
    }

    private void CitationRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DetailRow row) return;
        long? ms = row.StartMs;
        if (ms is null && row.SegmentIds.Count > 0)
            ms = _cloudSegments.FirstOrDefault(s => s.Id == row.SegmentIds[0])?.StartMs;
        if (ms is { } start) SeekMaster(start);
    }

    private void SeekMaster(long startMs)
    {
        try
        {
            var session = Player.MediaPlayer?.PlaybackSession;
            if (session is not null) session.Position = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
        }
        catch { }
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (_summaryPlain.Length == 0) { Status(InfoBarSeverity.Warning, Loc.T("DetWordNoAnalysis")); return; }
        try { ToClipboard(_summaryPlain); Status(InfoBarSeverity.Success, Loc.Tf("Det.Copied", _summaryPlain.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length)); }
        catch (COMException) { Status(InfoBarSeverity.Error, Loc.T("Det.ClipboardBusy")); }
    }

    private async void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        var backendId = _cloudBackendId ?? _backendId;
        if (backendId is null) { Status(InfoBarSeverity.Warning, Loc.T("DetWordNoAnalysis")); return; }
        if (!AppSession.TryCreateClient(out var client)) { Status(InfoBarSeverity.Warning, Loc.T("DetAnalyzeSignIn")); return; }
        var configured = AppStore.Repo.GetSetting("storage.path");
        var folder = string.IsNullOrWhiteSpace(configured) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : configured;
        var path = Path.Combine(folder, SlugFor(_meeting?.Title ?? MeetingTitle.Text) + ".summary.docx");
        if (File.Exists(path))
        {
            var confirm = new ContentDialog { Title = Loc.T("Det.OverwriteTitle"), Content = Loc.Tf("Det.OverwriteBody", path), PrimaryButtonText = Loc.T("Det.Overwrite"), CloseButtonText = Loc.T("Det.Cancel"), XamlRoot = XamlRoot };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        try
        {
            Directory.CreateDirectory(folder);
            await client.DownloadExportAsync(backendId, path);
            Status(InfoBarSeverity.Success, Loc.Tf("DetWordSaved", path));
        }
        catch (BackendApiException ex) when (ex.Code == "no_analysis")
        {
            Status(InfoBarSeverity.Warning, Loc.T("DetWordNoAnalysis"));
        }
        catch (BackendApiException ex)
        {
            if (ex.StatusCode == 401) AppSession.NoteAuthFailure(ex);
            Status(InfoBarSeverity.Error, Loc.Tf("DetWordFailed", DescribeBackendError(ex)));
        }
        catch (Exception ex)
        {
            Status(InfoBarSeverity.Error, Loc.Tf("DetWordFailed", DescribeBackendError(ex)));
        }
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        var backendId = _cloudBackendId ?? _backendId;
        if (backendId is null) return;
        var question = QaBox.Text.Trim();
        if (question.Length == 0) return;
        if (!AppSession.TryCreateClient(out var client)) { Status(InfoBarSeverity.Warning, Loc.T("DetAnalyzeSignIn")); return; }
        AskButton.IsEnabled = false;
        QaStatus.Text = Loc.T("Back.AskQueued");
        try
        {
            var job = await client.AskAsync(backendId, question);
            var terminal = await WaitForJobAsync(client, job.JobId);
            QaStatus.Text = terminal.State == "succeeded" ? Loc.T("Back.State.Ready") : Loc.T("Back.State.Failed");
            if (terminal.State == "succeeded")
            {
                QaBox.Text = "";
                var bundle = await client.GetAnalysisAsync(backendId);
                RenderSummary(bundle);
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

    // --- Row models ---

    public sealed class CloudLineRow
    {
        public string Time { get; set; } = "";
        public string Speaker { get; set; } = "";
        public string Text { get; set; } = "";
        public long StartMs { get; set; }
    }

    public sealed class DetailRow
    {
        public string Text { get; set; } = "";
        public string Detail { get; set; } = "";
        public long? StartMs { get; set; }
        public List<string> SegmentIds { get; set; } = new();
        public DetailRow(string text, string detail) { Text = text; Detail = detail; }
    }

    // --- Helpers ---

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

    // A filesystem-safe stem for the two export paths (transcript text, summary docx).
    private static string SlugFor(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var slug = new string(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && !invalid.Contains(c) ? c : '-').ToArray()).Trim('-');
        return slug.Length == 0 ? "meeting" : slug;
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

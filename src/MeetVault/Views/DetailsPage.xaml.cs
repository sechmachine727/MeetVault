using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeetVault.Core;
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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;

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
        foreach (var s in repoSpks) { _speakerIdByName[s.DisplayName] = s.Id; Speakers.Add(s.DisplayName); }
        if (Speakers.Count == 0) Speakers.Add("Speaker 1");
        var names = repoSpks.ToDictionary(s => s.Id, s => s.DisplayName);
        foreach (var s in repoSegs)
        {
            Lines.Add(new TranscriptLine(names.TryGetValue(s.SpeakerId, out var n) ? n : s.SpeakerId, s.Text));
            _originalText.Add(s.Text);
            _segmentIds.Add(s.Id);
            _segmentSpeakerIds.Add(s.SpeakerId);
            _timings.Add((s.Id, s.StartMs, s.EndMs));
        }

        // Array snapshot across the ABI; items mutate in place so no re-set is needed after load.
        TranscriptList.ItemsSource = Lines.ToArray();
        ShowPlayer();
        ShowPriorJob(_meeting);
        UpdateLineStatus();
        Unloaded += (_, _) => { try { Player.Source = null; } catch { } };
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

    private void SpeakerPicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox box || box.DataContext is not TranscriptLine line) return;
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

    private bool TryTranscript(out string text)
    {
        text = string.Join(Environment.NewLine, Lines.Select(l => $"{l.Speaker}: {l.Text}"));
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

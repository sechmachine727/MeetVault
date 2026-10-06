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

        var targetId = PendingDetails.MeetingId;
        PendingDetails.MeetingId = null;
        _meeting = targetId is null ? AppStore.Repo.ListMeetings().FirstOrDefault() : AppStore.Repo.Load(targetId);
        if (_meeting is null)
        {
            UpdateLineStatus();
            SetActionsEnabled(false);
            EmptyState.Text = "No meeting to show yet. Start one in Live and its transcript lands here.";
            EmptyState.Visibility = Visibility.Visible;
            TranscriptList.Visibility = Visibility.Collapsed;
            return;
        }

        MeetingTitle.Text = _meeting.Title;
        MeetingMeta.Text = _meeting.EndedAt is { } end
            ? $"{_meeting.StartedAt:g}  \u00b7  {FormatDuration(_meeting)}  \u00b7  {MeetingCards.StatusLabel(_meeting.Status)}"
            : $"{_meeting.StartedAt:g}  \u00b7  in progress  \u00b7  {MeetingCards.StatusLabel(_meeting.Status)}";

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
        if (m.EndedAt is not { } ended) return "in progress";
        var minutes = (int)Math.Round((ended - m.StartedAt).TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
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
        JobState.Text = $"Last result: {GeminiStateLabel(job.State)} \u00b7 {job.Attempts} attempt(s)";
        if (job.State == GeminiJobState.Succeeded && job.ResultPath is not null)
        {
            try { JobResult.Text = File.ReadAllText(job.ResultPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { JobResult.Text = "The saved result could not be read."; }
        }
        else JobResult.Text = job.LastError ?? "The job finished without a result.";
    }

    private static string GeminiStateLabel(GeminiJobState state) => state switch
    {
        GeminiJobState.Queued => "Queued",
        GeminiJobState.Uploading => "Uploading",
        GeminiJobState.Succeeded => "Succeeded",
        GeminiJobState.Failed => "Failed",
        _ => "Unknown",
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
        catch (Exception ex) { Status(InfoBarSeverity.Warning, $"Edit kept in memory; save failed: {Short(ex)}"); }
    }

    private void UpdateLineStatus()
    {
        if (_meeting is null) { LineStatus.Text = ""; return; }
        var edited = Lines.Where((line, i) => i < _originalText.Count && line.Text != _originalText[i]).Count();
        LineStatus.Text = Lines.Count == 0
            ? "No lines"
            : edited == 0 ? $"{Lines.Count} line(s)" : $"{Lines.Count} line(s) \u00b7 {edited} edited";
        EmptyState.Visibility = Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Lines.Count == 0) EmptyState.Text = _meeting is null
            ? "No meeting to show yet. Start one in Live and its transcript lands here."
            : "No transcript lines for this meeting yet.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTranscript(out var text)) return;
        try
        {
            ToClipboard(text);
            Status(InfoBarSeverity.Success, $"Copied {Lines.Count} line(s) to the clipboard.");
        }
        catch (COMException) { Status(InfoBarSeverity.Error, "The clipboard is busy. Try again."); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTranscript(out var text)) return;
        var configured = AppStore.Repo.GetSetting("storage.path");
        var folder = string.IsNullOrWhiteSpace(configured) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : configured;
        var path = Path.Combine(folder, FileNameFor(_meeting?.Title ?? "meeting"));
        if (File.Exists(path))
        {
            var confirm = new ContentDialog { Title = "Overwrite export?", Content = $"{path} already exists. Replace it?", PrimaryButtonText = "Overwrite", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        try
        {
            Directory.CreateDirectory(folder); // the configured folder may not exist yet
            File.WriteAllText(path, text);
            Status(InfoBarSeverity.Success, $"Saved {Lines.Count} line(s) to {path}.");
        }
        catch (DirectoryNotFoundException)
        {
            Status(InfoBarSeverity.Error, "The export folder does not exist and could not be created. Check the storage location in Settings.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status(InfoBarSeverity.Error, "Could not write the transcript file: " + Short(ex));
        }
    }

    private async void Gemini_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting is null || !TryTranscript(out _)) return;
        var key = AppStore.Secrets.Get("GeminiApiKey");
        if (key is not { Length: > 0 })
        {
            GeminiExpander.IsExpanded = true;
            JobState.Text = "No Gemini API key stored.";
            JobResult.Text = "Add your Gemini API key in Settings, then send again. The key is kept in Windows Credential Locker, never in a file.";
            Status(InfoBarSeverity.Warning, "Add a Gemini API key in Settings before sending.");
            return;
        }
        if (_meeting.MediaPath is null || !File.Exists(_meeting.MediaPath))
        {
            GeminiExpander.IsExpanded = true;
            JobState.Text = "No recording for this meeting.";
            JobResult.Text = "This meeting has no saved audio to upload.";
            Status(InfoBarSeverity.Warning, "This meeting has no saved audio to send.");
            return;
        }

        SendButton.IsEnabled = false;
        GeminiExpander.IsExpanded = true;
        JobState.Text = "Uploading \u2026";
        JobResult.Text = "";
        try
        {
            using var transport = new GeminiHttpTransport(key);
            var uploader = new GeminiUploader(AppStore.Secrets, new LocalMeetingFileReader(), transport);
            var job = await uploader.SendMeetingAsync(_meeting, JsonSerializer.Serialize(Lines.ToList(), TranscriptJsonContext.Default.ListTranscriptLine), ResultDirectory);
            AppStore.Repo.SaveJob(job);

            JobState.Text = $"Last result: {GeminiStateLabel(job.State)} \u00b7 {job.Attempts} attempt(s)";
            if (job.State == GeminiJobState.Succeeded && job.ResultPath is not null)
            {
                JobResult.Text = File.ReadAllText(job.ResultPath);
                Status(InfoBarSeverity.Success, "Gemini returned an analysis and it was saved locally.");
            }
            else
            {
                JobResult.Text = job.LastError ?? "The job finished without a result.";
                Status(InfoBarSeverity.Error, "Gemini job failed: " + (job.LastError ?? "unknown reason"));
            }
        }
        catch (Exception ex)
        {
            JobResult.Text = "The upload could not finish.";
            Status(InfoBarSeverity.Error, "Gemini send failed: " + Short(ex));
        }
        finally { SendButton.IsEnabled = true; }
    }

    private bool TryTranscript(out string text)
    {
        text = string.Join(Environment.NewLine, Lines.Select(l => $"{l.Speaker}: {l.Text}"));
        if (Lines.Count > 0) return true;
        Status(InfoBarSeverity.Warning, "The transcript is empty.");
        return false;
    }

    private static void ToClipboard(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static string ResultDirectory => Path.Combine(Path.GetTempPath(), "MeetVault", "gemini");

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
        UnauthorizedAccessException => "permission denied",
        IOException => "the file is in use or the disk is full",
        Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 8 } => "the data folder is read-only",
        _ => "something went wrong",
    };
}

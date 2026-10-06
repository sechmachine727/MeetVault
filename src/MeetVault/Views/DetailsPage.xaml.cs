using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using MeetVault.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace MeetVault.Views;

public sealed partial class DetailsPage : Page
{
    public ObservableCollection<TranscriptLine> Lines { get; } = new();
    public ObservableCollection<string> Speakers { get; } = new();

    // ponytail: demo transcript so edit/copy/export/Gemini have something to act on; swap for LocalMeetingRepository segments in P4.
    private static readonly (string Speaker, string Text)[] Seed =
    {
        ("Speaker 1", "Let's start with the October milestones. We shipped the recorder, the overlay is still in review."),
        ("Speaker 2", "Overlay review lands Thursday. I will bring the opacity and font defaults."),
        ("Speaker 1", "Speaker diarization is the open risk. It drops the quiet second speaker about one turn in five."),
        ("Speaker 3", "I can take the diarization tuning. The base model stays base.en-q5_1 on CPU for now."),
        ("Speaker 2", "Gemini summaries look good on the Northwind interview. Action items come out clean."),
        ("Speaker 1", "Then we keep it. Transcript edits stay in memory until the storage layer lands."),
    };

    private readonly List<string> _originalText = new();
    private MeetVault.Core.Meeting? _meeting;

    private const string DemoMediaPath = "demo://quarterly-roadmap-sync/audio.wav";
    private const string DemoMime = "audio/wav";

    private const string CannedResult = """
        {
          "fileId": "files/demo-quarterly",
          "model": "gemini-3.8-flash",
          "summary": "Roadmap sync confirms the recorder ships, the overlay review lands Thursday, and diarization remains the open risk.",
          "actionItems": [
            "Ship the overlay opacity and font defaults on Thursday.",
            "Tune diarization for quiet second speakers.",
            "Hold the transcript in memory until the storage layer lands."
          ]
        }
        """;

    public DetailsPage()
    {
        InitializeComponent();

        TranscriptList.ItemsSource = Lines;
        var first = AppStore.Repo.ListMeetings().FirstOrDefault();
        var repoSegs = first is null ? new List<Segment>() : AppStore.Repo.LoadSegments(first.Id).ToList();
        var repoSpks = first is null ? new List<Speaker>() : AppStore.Repo.LoadSpeakers(first.Id).ToList();
        if (repoSegs.Count > 0)
        {
            var names = repoSpks.ToDictionary(s => s.Id, s => s.DisplayName);
            foreach (var s in repoSpks) Speakers.Add(s.DisplayName);
            foreach (var s in repoSegs) { Lines.Add(new TranscriptLine(names.TryGetValue(s.SpeakerId, out var n) ? n : s.SpeakerId, s.Text)); _originalText.Add(s.Text); }
        }
        else
        {
            Speakers.Add("Speaker 1");
            Speakers.Add("Speaker 2");
            Speakers.Add("Speaker 3");
            foreach (var (speaker, text) in Seed)
            {
                Lines.Add(new TranscriptLine(speaker, text));
                _originalText.Add(text);
            }
        }

        _meeting = first;
        var card = AppStore.Cards().FirstOrDefault() ?? DemoMeetings.Cards[0];
        ShowPriorJob(first);
        MeetingTitle.Text = card.Title;
        MeetingMeta.Text = $"{card.When}  \u00b7  {card.Duration}  \u00b7  {card.Status}";
        UpdateLineStatus();
    }

    // Prior Gemini result survives restarts via the persisted job row.
    private void ShowPriorJob(MeetVault.Core.Meeting? meeting)
    {
        if (meeting is null) return;
        var job = AppStore.Repo.LoadJob(meeting.Id);
        if (job is null) return;
        GeminiExpander.IsExpanded = true;
        JobState.Text = $"Job {job.Id} · {job.State} · {job.Attempts} attempt(s)" + (job.ResultPath is null ? "" : $" · {job.ResultPath}");
        if (job.State == MeetVault.Core.GeminiJobState.Succeeded && job.ResultPath is not null)
        {
            try { JobResult.Text = File.ReadAllText(job.ResultPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { JobResult.Text = $"Result file unreadable: {ex.Message}"; }
        }
        else JobResult.Text = job.LastError ?? "The job finished without a result.";
    }

    private void SpeakerPicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox box || box.DataContext is not TranscriptLine line) return;
        box.ItemsSource = Speakers;
        box.SelectedItem = line.Speaker;
    }

    private void SpeakerPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.DataContext is TranscriptLine line && box.SelectedItem is string speaker)
            line.Speaker = speaker;
    }

    private void LineText_TextChanged(object sender, TextChangedEventArgs e) => UpdateLineStatus();

    private void UpdateLineStatus()
    {
        var edited = Lines.Where((line, i) => i < _originalText.Count && line.Text != _originalText[i]).Count();
        LineStatus.Text = Lines.Count == 0
            ? "No lines"
            : edited == 0 ? $"{Lines.Count} line(s)" : $"{Lines.Count} line(s) \u00b7 {edited} edited";
        EmptyState.Visibility = Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTranscript(out var text)) return;
        try
        {
            ToClipboard(text);
            Status(InfoBarSeverity.Success, $"Copied {Lines.Count} line(s) to the clipboard.");
        }
        catch (COMException ex)
        {
            Status(InfoBarSeverity.Error, $"Clipboard unavailable: {ex.Message}");
        }
    }

    // ponytail: writes straight to Documents with no FileSavePicker; add the picker when export location becomes a user choice.
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTranscript(out var text)) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FileNameFor((_meeting?.Title) ?? DemoMeetings.Cards[0].Title));
        if (File.Exists(path))
        {
            var confirm = new ContentDialog { Title = "Overwrite export?", Content = $"{path} already exists. Replace it?", PrimaryButtonText = "Overwrite", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        try
        {
            File.WriteAllText(path, text);
            ToClipboard(text);
            Status(InfoBarSeverity.Success, $"Saved {Lines.Count} line(s) to {path} and copied them to the clipboard.");
        }
        catch (IOException ex)
        {
            Status(InfoBarSeverity.Error, $"Export failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Status(InfoBarSeverity.Error, $"Export blocked: {ex.Message}");
        }
    }

    private async void Gemini_Click(object sender, RoutedEventArgs e)
    {
        if (!TryTranscript(out _)) return;

        SendButton.IsEnabled = false;
        GeminiExpander.IsExpanded = true;
        JobState.Text = "Queued \u2026";
        JobResult.Text = "";
        try
        {
            var uploader = new GeminiUploader(new CredentialLockerSecretStore(), new DemoFileReader(), new DemoTransport());
            var meeting = (_meeting ?? DemoMeetings.All[0]) with { MediaPath = DemoMediaPath, MediaMime = DemoMime };
            var job = await uploader.SendMeetingAsync(meeting, JsonSerializer.Serialize(Lines), ResultDirectory);
            AppStore.Repo.SaveJob(job);

            JobState.Text = $"Job {job.Id} \u00b7 {job.State} \u00b7 {job.Attempts} attempt(s)" + (job.ResultPath is null ? "" : $" \u00b7 {job.ResultPath}");
            if (job.State == GeminiJobState.Succeeded && job.ResultPath is not null)
            {
                JobResult.Text = File.ReadAllText(job.ResultPath);
                Status(InfoBarSeverity.Success, "Gemini job succeeded against the demo transport: no network call, no media uploaded.");
            }
            else
            {
                JobResult.Text = job.LastError ?? "The job finished without a result.";
                Status(InfoBarSeverity.Error, $"Gemini job {job.State}: {job.LastError}");
            }
        }
        catch (IOException ex)
        {
            JobResult.Text = ex.Message;
            Status(InfoBarSeverity.Error, $"Gemini job could not finish: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            JobResult.Text = ex.Message;
            Status(InfoBarSeverity.Error, $"Gemini result could not be written: {ex.Message}");
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private bool TryTranscript(out string text)
    {
        text = string.Join(Environment.NewLine, Lines.Select(l => $"{l.Speaker}: {l.Text}"));
        if (Lines.Count > 0) return true;
        Status(InfoBarSeverity.Warning, "Nothing to act on: the transcript is empty.");
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
        return slug + ".transcript.txt";
    }

    private void Status(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    // ponytail: demo file-reader + transport until P4 engines land; key is real (Credential Locker) and jobs persist.

    private sealed class DemoFileReader : IMeetingFileReader
    {
        public long SizeOf(string path) => 12_582_912;
        public Stream OpenRead(string path) => new MemoryStream(new byte[1024]);
    }

    private sealed class DemoTransport : IGeminiTransport
    {
        public Task<string> UploadFileAsync(string path, Stream content, string mime, CancellationToken ct)
            => Task.FromResult("files/demo-quarterly");

        public Task<string> InteractAsync(string fileId, string transcriptJson, CancellationToken ct)
            => Task.FromResult(CannedResult);
    }
}








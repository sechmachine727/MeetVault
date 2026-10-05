using System.Collections.ObjectModel;
using MeetVault.Core;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class LivePage : Page
{
    public ObservableCollection<TranscriptLine> Lines { get; } = new();
    public ObservableCollection<string> Speakers { get; } = new();

    private readonly FakeStore _store = new();
    private readonly MeetingSession _session;

    public LivePage()
    {
        InitializeComponent();
        TranscriptList.ItemsSource = Lines;
        SpeakerList.ItemsSource = Speakers;

        _session = new MeetingSession(new FakeAudioCapture(), new FakeTranscriber(), new FakeDiarizer(), new FakeGenderLabeler(), _store);
        _session.LiveSegment += OnLiveSegment;

        Loaded += (_, _) =>
        {
            EngineStatus.Text = "Fake engines wired to Core.MeetingSession.";
            Speakers.Add("Speaker 1");
            AddLine("Speaker 1", "Demo line: Core.MeetingSession is driving this list.");
        };
    }

    // ponytail: a real transcriber raises this off the UI thread; hop before touching Lines.
    private void OnLiveSegment(Segment segment)
        => DispatcherQueue.TryEnqueue(() => AddLine(segment.SpeakerId, segment.Text));

    private void AddLine(string speaker, string text) => Lines.Add(new TranscriptLine(speaker, text));

    private void Status(string message) { StatusBar.Message = message; StatusBar.IsOpen = true; }

    private async void Pause_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { await _session.PauseAsync(); Status("Paused: capture halted with a gap."); }
        catch (InvalidOperationException) { Status("Nothing to pause: this demo session was never started."); }
    }

    private async void Resume_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e){ try { await _session.ResumeAsync(); Status("Resumed: capture continues after the gap."); } catch (InvalidOperationException) { Status("Nothing to resume: this demo session was never started."); } } private void Overlay_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { OverlayWindow.ShowOrActivate(); }

    private async void Stop_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { await _session.StopAsync(); Status($"Stopped: {_store.SavedSegments} segment(s) finalized to local storage."); }
        catch (InvalidOperationException) { Status("Nothing to stop: this demo session was never started."); }
    }

    private void Speaker_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { Status("Rename: click-to-rename syncs both ways once the session binds (P4)."); }

    // ponytail: P4 replaces these with the real engines; the page only sees the Core interfaces.
    private sealed class FakeAudioCapture : IAudioCapture
    {
        public bool IsCapturing { get; private set; }
        public Task StartAsync(string micId, string appSource, CancellationToken ct) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
    }

    private sealed class FakeTranscriber : ISpeechTranscriber
    {
#pragma warning disable CS0067 // no emitter yet: this page seeds a demo line instead of streaming audio
        public event Action<Segment>? SegmentReady;
#pragma warning restore CS0067
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
    }

    private sealed class FakeDiarizer : ISpeakerDiarizer
    {
        private int _turn;
        public string AssignTurn(Segment segment) => _turn++ % 2 == 0 ? "Speaker 1" : "Speaker 2";
    }

    private sealed class FakeGenderLabeler : IGenderLabeler
    {
        public string Label(string speakerId, Gender estimate) => "Unknown";
    }

    private sealed class FakeStore : IMeetingStore
    {
        public int SavedSegments { get; private set; }
        public void Save(Meeting meeting, IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments) => SavedSegments = segments.Count;
    }
}


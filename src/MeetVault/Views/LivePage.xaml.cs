using System.Collections.ObjectModel;
using MeetVault.Core;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class LivePage : Page
{
    public ObservableCollection<TranscriptLine> Lines { get; } = new();
    public ObservableCollection<string> Speakers { get; } = new();

    private readonly MeetingSession _session;
    private readonly WhisperTranscriber? _whisper;
    private readonly NAudioCapture? _capture;
    private readonly List<byte> _window = new();
    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "models");

    public LivePage()
    {
        InitializeComponent();
        TranscriptList.ItemsSource = Lines;
        SpeakerList.ItemsSource = Speakers;

        // Real engines when models + mic exist; fakes otherwise (status line says which).
        var whisperPath = Path.Combine(ModelDir, "ggml-base.bin");
        var segPath = Path.Combine(ModelDir, "sherpa-seg.onnx");
        var embPath = Path.Combine(ModelDir, "sherpa-emb.onnx");
        ISpeechTranscriber transcriber = new FakeTranscriber();
        IAudioCapture audio = new FakeAudioCapture();
        ISpeakerDiarizer diarizer = new FakeDiarizer();
        var mode = "demo (no models)";
        if (File.Exists(whisperPath) && File.Exists(segPath) && File.Exists(embPath))
        {
            try
            {
                _whisper = new WhisperTranscriber(whisperPath);
                _capture = new NAudioCapture();
                var dz = new SherpaDiarizer(segPath, embPath);
                transcriber = _whisper;
                audio = _capture;
                diarizer = new WindowDiarizer(dz);
                _capture.PcmReady += OnPcm;
                mode = "live: whisper base + sherpa diarizer";
            }
            catch { /* fall through to fakes */ }
        }
        _session = new MeetingSession(audio, transcriber, diarizer, new CounterGenderLabeler(), AppStore.Repo);
        _session.LiveSegment += OnLiveSegment;

        var m = mode;
        Loaded += async (_, _) =>
        {
            EngineStatus.Text = m;
            try { await _session.StartAsync("0", "", CancellationToken.None); }
            catch (Exception ex) { Status($"Could not start capture: {ex.Message}"); }
        };
    }

    // Accumulates mic PCM into ~10 s windows and transcribes each finalized window.
    private async void OnPcm(byte[] chunk)
    {
        if (_whisper is null || !_whisper.IsRunning) return;
        lock (_window) _window.AddRange(chunk);
        if (_window.Count < 16000 * 2 * 10) return;
        byte[] window;
        lock (_window) { window = _window.ToArray(); _window.Clear(); }
        try { await _whisper.TranscribeWindowAsync(window, 0, "live"); }
        catch { /* next window retries; audio file keeps everything */ }
    }

    private void OnLiveSegment(Segment segment)
        => DispatcherQueue.TryEnqueue(() => AddLine(segment.SpeakerId, segment.Text));

    private void AddLine(string speaker, string text) => Lines.Add(new TranscriptLine(speaker, text));

    private void Status(string message) { StatusBar.Message = message; StatusBar.IsOpen = true; }

    private async void Pause_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { await _session.PauseAsync(); Status("Paused: capture halted with a gap."); }
        catch (InvalidOperationException) { Status("Nothing to pause: session never started."); }
    }

    private async void Resume_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e){ try { await _session.ResumeAsync(); Status("Resumed: capture continues after the gap."); } catch (InvalidOperationException) { Status("Nothing to resume: session never started."); } }
    private void Overlay_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { OverlayWindow.ShowOrActivate(); }

    private async void Stop_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { await _session.StopAsync(); Status($"Stopped: {_session.Segments.Count} segment(s) saved to the local repository."); }
        catch (InvalidOperationException) { Status("Nothing to stop: session never started."); }
    }

    private void Speaker_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { Status("Rename: click-to-rename syncs both ways once the session binds (P4)."); }

    private sealed class FakeAudioCapture : IAudioCapture
    {
        public bool IsCapturing { get; private set; }
        public Task StartAsync(string micId, string appSource, CancellationToken ct) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
    }

    private sealed class FakeTranscriber : ISpeechTranscriber
    {
        public event Action<Segment>? SegmentReady;
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; SegmentReady?.Invoke(new Segment("demo", "spk_1", 0, 1000, "Demo line: add models to models/ for live transcription.", true)); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
    }

    private sealed class FakeDiarizer : ISpeakerDiarizer
    {
        public string AssignTurn(Segment segment) => "spk_1";
    }

    // Bridges window-level sherpa assignment into the session's per-segment path.
    private sealed class WindowDiarizer : ISpeakerDiarizer
    {
        private readonly SherpaDiarizer _inner;
        private string _current = "spk_1";
        public WindowDiarizer(SherpaDiarizer inner) { _inner = inner; }
        public void AssignWindow(float[] samples) { try { _current = _inner.AssignWindow(samples); } catch { } }
        public string AssignTurn(Segment segment) => _current;
    }
}


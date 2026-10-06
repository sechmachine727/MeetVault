using System.Collections.ObjectModel;
using MeetVault.Core;
using Microsoft.UI.Xaml.Controls;

namespace MeetVault.Views;

public sealed partial class LivePage : Page
{
    public ObservableCollection<TranscriptLine> Lines { get; } = new();
    public ObservableCollection<string> Speakers { get; } = new();

private MeetingSession _session = null!;
    private readonly WhisperTranscriber? _whisper;
    private readonly NAudioCapture? _capture;
    private readonly List<byte> _window = new();
    private WindowDiarizer? _windowDiarizer;
    private readonly ISpeechTranscriber _transcriber;
    private readonly IAudioCapture _audio;
    private readonly ISpeakerDiarizer _diarizer;
    private static readonly string ModelDir = Path.Combine(AppContext.BaseDirectory, "models");
    private long _windowOffsetMs; // M1: accumulate so window N segments carry meeting-relative timings.
    // ponytail: single persisted mic id; device picker UI add when >1 mic matters.
    private static string MicId => AppStore.Repo.GetSetting("audio.mic") ?? "0";
    private static string AppSource => AppStore.Repo.GetSetting("audio.source") ?? "";

    public LivePage()
    {
        InitializeComponent();
        TranscriptList.ItemsSource = Lines;
        SpeakerList.ItemsSource = Speakers;

        // Real engines when models exist; fakes otherwise (status line says which).
        ISpeechTranscriber transcriber = new FakeTranscriber();
        IAudioCapture audio = new FakeAudioCapture();
        ISpeakerDiarizer diarizer = new FakeDiarizer();
        _mode = "demo (no models)";
        // Engine construction (ONNX load) stays inside the guard: any throw keeps fakes.
        try
        {
            if (EngineModels.Available)
            {
                var modelFile = AppStore.Repo.GetSetting("transcription.model") == "base" ? "ggml-base.bin" : "ggml-tiny.bin";
                _whisper = new WhisperTranscriber(Path.Combine(ModelDir, modelFile));
                _capture = new NAudioCapture();
                var dz = new SherpaDiarizer(Path.Combine(ModelDir, "sherpa-seg.onnx"), Path.Combine(ModelDir, "sherpa-emb.onnx"));
                transcriber = _whisper;
                audio = _capture;
                _windowDiarizer = new WindowDiarizer(dz); diarizer = _windowDiarizer;
                _capture.PcmReady += OnPcm;
                _mode = "live: whisper tiny + sherpa diarizer (base reserved for refinement)";
            }
        }
        catch (Exception ex) { _mode = "demo (engine init failed: " + ex.GetType().Name + ")"; }
        _transcriber = transcriber; _audio = audio; _diarizer = diarizer;

        Loaded += OnLoaded;
    }

    private readonly string _mode;

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        EngineStatus.Text = _mode;
        try
        {
            var id = PendingRecovery.MeetingId;
            PendingRecovery.MeetingId = null;
            if (id is not null)
            {
                _session = await MeetingSession.RecoverAsync(id, _audio, _transcriber, _diarizer, new CounterGenderLabeler(), AppStore.Repo, MicId, AppSource);
                foreach (var s in _session.Segments) AddLine(s.SpeakerId, s.Text);
                foreach (var sp in _session.Speakers) Speakers.Add(sp.DisplayName);
                Status($"Recovered {_session.Segments.Count} segment(s) from the interrupted meeting.");
            }
            else
            {
                _session = new MeetingSession(_audio, _transcriber, _diarizer, new CounterGenderLabeler(), AppStore.Repo);
                await _session.StartAsync(MicId, AppSource, CancellationToken.None);
            }
            _session.LiveSegment += OnLiveSegment;
        }
        catch (Exception ex) { Status($"Could not start capture: {ex.Message}"); }
    }

    // Accumulates mic PCM into ~10 s windows and transcribes each finalized window.
    private async void OnPcm(byte[] chunk)
    {
        if (_whisper is null || !_whisper.IsRunning) return;
        lock (_window) _window.AddRange(chunk);
        if (_window.Count < 16000 * 2 * 10) return;
        byte[] window;
        lock (_window) { window = _window.ToArray(); _window.Clear(); }
        _windowDiarizer?.AssignWindowSamples(window);
        // WindowMs matches the 10 s accumulation gate above; offset advances per finalized window.
        const long WindowMs = 10_000;
        var start = _windowOffsetMs;
        _windowOffsetMs += WindowMs;
        try { await _whisper.TranscribeWindowAsync(window, start, "live"); }
        catch { /* next window retries; audio file keeps everything */ }
    }

    private void OnLiveSegment(Segment segment)
        => DispatcherQueue.TryEnqueue(() => AddLine(segment.SpeakerId, segment.Text));

    private void AddLine(string speaker, string text) => Lines.Add(new TranscriptLine(speaker, text));

    private void Status(string message) { StatusBar.Message = message; StatusBar.IsOpen = true; }

    private async void Pause_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { if (_session is null) Status("Nothing to pause: session never started."); else { await _session.PauseAsync(); Status("Paused: capture halted with a gap."); } }
        catch (InvalidOperationException) { Status("Nothing to pause: session never started."); }
    }

    private async void Resume_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e){ try { if (_session is null) Status("Nothing to resume: session never started."); else { await _session.ResumeAsync(); Status("Resumed: capture continues after the gap."); } } catch (InvalidOperationException) { Status("Nothing to resume: session never started."); } }
    private void Overlay_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) { OverlayWindow.ShowOrActivate(); }

    private async void Stop_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try { if (_session is null) Status("Nothing to stop: session never started."); else { await _session.StopAsync(); Status($"Stopped: {_session.Segments.Count} segment(s) saved to the local repository."); } }
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
        public void AssignWindowSamples(byte[] pcm16) { try { var f = new float[pcm16.Length / 2]; for (var i = 0; i < f.Length; i++) f[i] = System.BitConverter.ToInt16(pcm16, 2 * i) / 32768f; _current = _inner.AssignWindow(f); } catch { } }
        public string AssignTurn(Segment segment) => _current;
    }
}







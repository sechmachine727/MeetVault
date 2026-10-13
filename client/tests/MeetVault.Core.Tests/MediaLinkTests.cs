using MeetVault.Core;

namespace MeetVault.Core.Tests;

// FR6/FR7: the recorded media file must be linked to the meeting so Details can play it.
public sealed class MediaLinkTests
{
    private sealed class AudioWithFile : IAudioCapture
    {
        public bool IsCapturing { get; private set; }
        public string? MediaPath { get; set; }
        public Task StartAsync(string micId, string appSource, CancellationToken ct) { MediaPath = "/tmp/rec.wav"; IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
        public Task FinishAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
    }

    // Mirrors NAudioCapture's contract: MediaPath is the working WAV until FinishAsync
    // compacts it, after which it is the .m4a the meeting should keep.
    private sealed class AudioSwitchingToCompacted : IAudioCapture
    {
        private readonly string _wav;
        private readonly string _m4a;
        private bool _finished;
        public AudioSwitchingToCompacted(string wav, string m4a) { _wav = wav; _m4a = m4a; }
        public bool IsCapturing { get; private set; }
        public string? MediaPath => _finished ? _m4a : _wav;
        public Task StartAsync(string micId, string appSource, CancellationToken ct) { IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
        public Task FinishAsync(CancellationToken ct) { _finished = true; IsCapturing = false; return Task.CompletedTask; }
    }

    private sealed class RecordingStore : IMeetingStore
    {
        public Meeting? Saved;
        public void Save(Meeting meeting, IReadOnlyList<Speaker> speakers, IReadOnlyList<Segment> segments) => Saved = meeting;
        public IReadOnlyList<Segment> LoadSegments(string meetingId) => Array.Empty<Segment>();
        public IReadOnlyList<Speaker> LoadSpeakers(string meetingId) => Array.Empty<Speaker>();
        public IReadOnlyList<Meeting> LoadUnfinished() => Array.Empty<Meeting>();
    }

    private sealed class T : ISpeechTranscriber
    {
#pragma warning disable CS0067
        public event Action<Segment>? SegmentReady;
    public event Action<string>? Failed;
#pragma warning restore CS0067
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Stop_LinksMediaPathAndMime()
    {
        var audio = new AudioWithFile();
        var store = new RecordingStore();
        var session = new MeetingSession(audio, new T(), store);
        await session.StartAsync("0", "");
        await session.StopAsync();
        Assert.Equal("/tmp/rec.wav", store.Saved!.MediaPath);
        Assert.Equal("audio/wav", store.Saved.MediaMime);
    }

    // Segment ids are namespaced by the meeting id; it must be set so two meetings
    // never mint the same transcript_segments.id (a global primary key).
    [Fact]
    public async Task MeetingId_IsSetAfterStart()
    {
        var session = new MeetingSession(new AudioWithFile(), new T(), new RecordingStore());
        await session.StartAsync("0", "");
        Assert.False(string.IsNullOrEmpty(session.MeetingId));
    }

    // The superseded working file is removed only after the row points at the compacted one,
    // so a crash or a failed save can never leave a meeting listed with no recording on disk.
    [Fact]
    public async Task Stop_RemovesWorkingFile_AfterPersistingTheCompactedOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "m.wav");
        var m4a = Path.Combine(dir, "m.m4a");
        File.WriteAllBytes(wav, new byte[1000]);
        File.WriteAllBytes(m4a, new byte[400]);
        try
        {
            var store = new RecordingStore();
            var session = new MeetingSession(new AudioSwitchingToCompacted(wav, m4a), new T(), store);
            await session.StartAsync("0", "");
            await session.StopAsync();

            Assert.Equal(m4a, store.Saved!.MediaPath);
            Assert.Equal("audio/mp4", store.Saved.MediaMime);
            Assert.True(File.Exists(m4a));
            Assert.False(File.Exists(wav), "the superseded working file should be gone once the row is saved");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

using MeetVault.Core;

namespace MeetVault.Core.Tests;

// FR6/FR7: the recorded media file must be linked to the meeting so Details can play
// it and Gemini can upload it.
public sealed class MediaLinkTests
{
    private sealed class AudioWithFile : IAudioCapture
    {
        public bool IsCapturing { get; private set; }
        public string? MediaPath { get; set; }
        public Task StartAsync(string micId, string appSource, CancellationToken ct) { MediaPath = "/tmp/rec.wav"; IsCapturing = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsCapturing = false; return Task.CompletedTask; }
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
#pragma warning restore CS0067
        public bool IsRunning { get; private set; }
        public Task StartAsync(CancellationToken ct) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { IsRunning = false; return Task.CompletedTask; }
    }

    private sealed class D : ISpeakerDiarizer { public string AssignTurn(Segment s) => "spk_1"; }
    private sealed class G : IGenderLabeler { public string Label(string id, Gender e) => "Speaker 1"; }

    [Fact]
    public async Task Stop_LinksMediaPathAndMime()
    {
        var audio = new AudioWithFile();
        var store = new RecordingStore();
        var session = new MeetingSession(audio, new T(), new D(), new G(), store);
        await session.StartAsync("0", "");
        await session.StopAsync();
        Assert.Equal("/tmp/rec.wav", store.Saved!.MediaPath);
        Assert.Equal("audio/wav", store.Saved.MediaMime);
    }
}

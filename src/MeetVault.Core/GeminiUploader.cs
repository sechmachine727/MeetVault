namespace MeetVault.Core;

public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
}

public interface IMeetingFileReader
{
    long SizeOf(string path);
    Stream OpenRead(string path);
}

public interface IGeminiTransport
{
    Task<string> UploadFileAsync(string path, Stream content, string mime, CancellationToken ct);
    Task<string> InteractAsync(string fileId, string transcriptJson, CancellationToken ct);
}

// ponytail: no SDK dependency; Files-API + interactions over small testable seams.
public sealed class GeminiUploader
{
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024; // 2 GB per-file ceiling until quota confirms otherwise
    private readonly ISecretStore _secrets;
    private readonly IMeetingFileReader _files;
    private readonly IGeminiTransport _transport;

    public GeminiUploader(ISecretStore secrets, IMeetingFileReader files, IGeminiTransport transport)
    {
        _secrets = secrets; _files = files; _transport = transport;
    }

    public async Task<GeminiJob> SendMeetingAsync(Meeting meeting, string transcriptJson, string resultDir, CancellationToken ct = default)
    {
        var job = new GeminiJob(Guid.NewGuid().ToString("n"), meeting.Id, GeminiJobState.Queued, 0, null, null);
        if (meeting.MediaPath is null) return job with { State = GeminiJobState.Failed, LastError = "No media file saved for this meeting." };
        if (_secrets.Get("GeminiApiKey") is not { Length: > 0 }) return job with { State = GeminiJobState.Failed, LastError = "Gemini API key missing. Add it in Settings." };
        if (_files.SizeOf(meeting.MediaPath) > MaxFileBytes) return job with { State = GeminiJobState.Failed, LastError = "File over 2 GB refused before upload." };
        job = job with { State = GeminiJobState.Uploading, Attempts = 1 };
        try
        {
            using var stream = _files.OpenRead(meeting.MediaPath);
            var fileId = await _transport.UploadFileAsync(meeting.MediaPath, stream, meeting.MediaMime ?? "application/octet-stream", ct);
            var resultJson = await _transport.InteractAsync(fileId, transcriptJson, ct);
            Directory.CreateDirectory(resultDir);
            var path = Path.Combine(resultDir, meeting.Id + ".gemini.json");
            await File.WriteAllTextAsync(path, resultJson, ct);
            return job with { State = GeminiJobState.Succeeded, ResultPath = path };
        }
        catch (Exception ex)
        {
            return job with { State = GeminiJobState.Failed, LastError = ex.Message }; // originals untouched; retry is manual
        }
    }
}


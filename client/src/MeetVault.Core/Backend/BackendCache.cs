namespace MeetVault.Core.Backend;

// On-disk cache of the newest server transcript/analysis per meeting, plus downloaded media.
// Atomic writes (tmp + move) so a crash can never leave half a JSON document; mirrors the
// Gemini-result pattern (%APPDATA%\MeetVault\backend\).
public sealed class BackendCache
{
    private readonly string _root;

    public BackendCache(string root) => _root = root;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetVault", "backend");

    public string MediaPath(string assetId, string extension)
    {
        var dir = Path.Combine(_root, "media");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Safe(assetId) + (string.IsNullOrEmpty(extension) ? "" : extension));
    }

    public void SaveTranscript(string meetingId, string json) => Write(Safe(meetingId) + ".transcript.json", json);
    public string? LoadTranscript(string meetingId) => Read(Safe(meetingId) + ".transcript.json");
    public void SaveAnalysis(string meetingId, string json) => Write(Safe(meetingId) + ".analysis.json", json);
    public string? LoadAnalysis(string meetingId) => Read(Safe(meetingId) + ".analysis.json");

    private void Write(string fileName, string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
        AppLog.Info("backend: cached " + path);
    }

    private string? Read(string fileName)
    {
        try
        {
            var path = Path.Combine(_root, fileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) { AppLog.Warn("backend: cache read failed: " + ex.Message); return null; }
    }

    // Every non-alphanumeric character maps to "_" rather than being dropped, so ids that differ
    // only in separators stop sharing one file. The mapping is NOT injective ("a b" and "a/b" map
    // alike); ids in this app are timestamp-hex + suffix, so collisions do not occur in practice
    // (review 2).
    private static string Safe(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}

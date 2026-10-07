using System.Text;

namespace MeetVault.Core;

public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4, Off = 5 }

// Single-file app log. Level is configurable (Settings -> logging.level, default Debug).
// Best-effort: a logging failure never throws into the caller.
public static class AppLog
{
    private static readonly object Gate = new();
    private static LogLevel _level = LogLevel.Debug;
    private static string _path = DefaultPath();
    private const long MaxBytes = 2_000_000;

    public static string Path => _path;

    private static string DefaultPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MeetVault", "app.log");

    public static void Configure(string? levelName, string? path = null)
    {
        _level = Parse(levelName);
        if (!string.IsNullOrWhiteSpace(path)) _path = path!;
    }

    public static LogLevel Parse(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" or "" => LogLevel.Debug,
        "info" or "information" => LogLevel.Info,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        "off" or "none" => LogLevel.Off,
        _ => LogLevel.Debug,
    };

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warn => "warning",
        LogLevel.Error => "error",
        _ => "off",
    };

    public static void Trace(string message) => Write(LogLevel.Trace, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, message + " :: " + ex.GetType().Name + ": " + ex.Message);

    public static void Write(LogLevel level, string message)
    {
        if (level < _level || _level == LogLevel.Off) return;
        try
        {
            lock (Gate)
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) Roll();
                var line = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("o")).Append(' ')
                    .Append('[').Append(LevelName(level).ToUpperInvariant()).Append("] ")
                    .Append(message).Append(Environment.NewLine).ToString();
                File.AppendAllText(_path, line);
            }
        }
        catch { /* logging never breaks the app */ }
    }

    private static void Roll()
    {
        try
        {
            var old = _path + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(_path, old);
        }
        catch { }
    }
}


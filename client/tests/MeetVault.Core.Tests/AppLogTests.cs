using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The app log must be configurable, level-filtered, and never throw.
public sealed class AppLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
    public void Dispose() { try { AppLog.Configure("debug"); if (File.Exists(_path)) File.Delete(_path); } catch { } }

    [Fact]
    public void DebugLevel_WritesInfoAndDebug_NotTrace()
    {
        AppLog.Configure("debug", _path);
        AppLog.Trace("trace-line");
        AppLog.Debug("debug-line");
        AppLog.Info("info-line");
        var text = File.ReadAllText(_path);
        Assert.Contains("debug-line", text);
        Assert.Contains("info-line", text);
        Assert.DoesNotContain("trace-line", text);
    }

    [Fact]
    public void OffLevel_WritesNothing()
    {
        AppLog.Configure("off", _path);
        AppLog.Error("error-line");
        Assert.False(File.Exists(_path) && File.ReadAllText(_path).Contains("error-line"));
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("", LogLevel.Debug)]
    [InlineData("info", LogLevel.Info)]
    [InlineData("warn", LogLevel.Warn)]
    [InlineData("off", LogLevel.Off)]
    public void Parse_MapsNames(string name, LogLevel expected) => Assert.Equal(expected, AppLog.Parse(name));
}


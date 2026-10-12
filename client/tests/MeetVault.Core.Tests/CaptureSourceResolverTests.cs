using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class CaptureSourceResolverTests
{
    private static CaptureSource D(string id) => new("display", id, id + " (2560x1440)");
    private static CaptureSource W(string exe, string title) => new("window", exe, exe + " — " + title);

    private static readonly CaptureSource[] Displays = { D(@"\\.\DISPLAY1"), D(@"\\.\DISPLAY2") };
    private static readonly CaptureSource[] NoWindows = Array.Empty<CaptureSource>();

    [Fact]
    public void StoredDisplay_IsHonored()
    {
        var r = CaptureSourceResolver.Resolve(@"display:\\.\DISPLAY2", Displays, NoWindows, out var reason);
        Assert.Equal(@"\\.\DISPLAY2", r.Id);
        Assert.Equal("", reason);
    }

    [Fact]
    public void NullOrEmptyStored_PrimaryDisplayWithReason()
    {
        var r1 = CaptureSourceResolver.Resolve(null, Displays, NoWindows, out var reason1);
        Assert.Equal(Displays[0].Id, r1.Id);
        Assert.Equal("no source stored", reason1);

        var r2 = CaptureSourceResolver.Resolve("  ", Displays, NoWindows, out var reason2);
        Assert.Equal(Displays[0].Id, r2.Id);
        Assert.Equal("no source stored", reason2);
    }

    [Fact]
    public void RetitledWindow_MatchedByPrefix()
    {
        var windows = new[] { W("notepad", "Untitled - Notepad") };
        var r = CaptureSourceResolver.Resolve("window:notepad:Untitled", Displays, windows, out var reason);
        Assert.Equal("window", r.Kind);
        Assert.Equal("notepad", r.Id);
        Assert.Equal("Untitled - Notepad", CaptureSourceResolver.LabelTitle(r));
        Assert.Equal("", reason);

        // The other direction: the stored title is longer than the live one.
        var r2 = CaptureSourceResolver.Resolve("window:notepad:Untitled - Notepad (admin)", Displays, windows, out _);
        Assert.Equal("notepad", r2.Id);
    }

    [Fact]
    public void NoTitleStored_AnyWindowOfExe()
    {
        var windows = new[] { W("chrome", "Inbox"), W("code", "plan.md") };
        var r = CaptureSourceResolver.Resolve("window:code:", Displays, windows, out var reason);
        Assert.Equal("code", r.Id);
        Assert.Equal("", reason);
    }

    [Fact]
    public void MissingWindow_FallsBackWithReason()
    {
        var windows = new[] { W("notepad", "Something else entirely") };
        var r = CaptureSourceResolver.Resolve("window:notepad:Gone", Displays, windows, out var reason);
        Assert.Equal(Displays[0].Id, r.Id);
        Assert.Equal("stored source missing", reason);
    }

    [Fact]
    public void MissingDisplay_FallsBackWithReason()
    {
        var r = CaptureSourceResolver.Resolve(@"display:\\.\DISPLAY9", Displays, NoWindows, out var reason);
        Assert.Equal(Displays[0].Id, r.Id);
        Assert.Equal("stored source missing", reason);
    }

    [Fact]
    public void GarbageStored_FallsBackWithReason()
    {
        var r = CaptureSourceResolver.Resolve("nonsense", Displays, NoWindows, out var reason);
        Assert.Equal(Displays[0].Id, r.Id);
        Assert.Equal("stored source missing", reason);
    }

    [Fact]
    public void Format_RoundTripsThroughResolve()
    {
        var window = W("notepad", "Untitled - Notepad");
        var stored = CaptureSourceResolver.Format(window);
        Assert.Equal("window:notepad:Untitled - Notepad", stored);
        var r = CaptureSourceResolver.Resolve(stored, Displays, new[] { window }, out _);
        Assert.Equal("notepad", r.Id);
        Assert.Equal("Untitled - Notepad", CaptureSourceResolver.LabelTitle(r));

        var display = D(@"\\.\DISPLAY2");
        Assert.Equal(@"display:\\.\DISPLAY2", CaptureSourceResolver.Format(display));
    }

    [Fact]
    public void WindowsAndDisplays_EnumerateThisSession()
    {
        // Smoke-level: the P/Invoke paths run without throwing and find this display.
        var displays = CaptureSourceResolver.Displays();
        Assert.NotEmpty(displays);
        Assert.All(displays, d => Assert.Equal("display", d.Kind));
        var windows = CaptureSourceResolver.Windows();
        Assert.All(windows, w => Assert.Equal("window", w.Kind));
        // Our own process must be excluded.
        Assert.DoesNotContain(windows, w => w.Id.Contains("testhost"));
    }
}

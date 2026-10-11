using System.Diagnostics;
using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The media root may be a junction the user created to relocate recordings (another
// drive). The boundary then IS the junction's final target: files under it stay managed
// (deleting the meeting must still remove their audio), while a junction BELOW the root
// is still refused (a row must not be able to redirect the delete outside the boundary).
public sealed class MediaRootJunctionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
    private readonly List<string> _links = new();

    public MediaRootJunctionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Remove junctions newest-first (a link inside another link must go before its
        // parent) so the recursive cleanup never walks through one.
        for (var i = _links.Count - 1; i >= 0; i--) Cmd("rmdir", "\"" + _links[i] + "\"");
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Link(string link, string target)
    {
        var (code, output) = Cmd("mklink", "/J \"" + link + "\" \"" + target + "\"");
        Assert.True(code == 0, "mklink /J failed: " + output);
        _links.Add(link);
    }

    private static (int Code, string Output) Cmd(string verb, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + verb + " " + args)
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static Meeting NewMeeting(string id, string? media) =>
        new(id, "t-" + id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MeetingStatus.Ready, media, "audio/m4a");

    private static void Save(LocalMeetingRepository repo, string id, string media) =>
        repo.Save(NewMeeting(id, media),
            new[] { new Speaker("spk_1", "Speaker 1") },
            new[] { new Segment("seg-" + id, "spk_1", 0, 1000, "hi", true) });

    [Fact]
    public void DeleteMeetings_JunctionRoot_ResolvesBoundary_DeletesFileAndRow()
    {
        if (!OperatingSystem.IsWindows()) return;   // mklink /J is Windows-only
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_dir, "link");
        Link(link, target);

        using var repo = new LocalMeetingRepository(Path.Combine(_dir, "t1.db"), link);
        var media = Path.Combine(link, "a.m4a");
        File.WriteAllBytes(media, new byte[] { 1, 2, 3 });
        Save(repo, "a", media);

        // Before this fix the root-reparse refusal made every relocated-root recording
        // read as unmanaged: the row would be deleted with the audio silently left behind.
        Assert.True(repo.IsManagedMedia(media));

        var result = repo.DeleteMeetings(new[] { "a" });

        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        Assert.Empty(result.FailedIds);
        Assert.Empty(result.SkippedMedia);
        Assert.False(File.Exists(media));
        Assert.False(File.Exists(Path.Combine(target, "a.m4a")));   // the same bytes, seen through the target
        Assert.Null(repo.Load("a"));
    }

    [Fact]
    public void DeleteMeetings_JunctionBelowRoot_Refused_FileUntouched_RowDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_dir, "link");
        Link(link, target);
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var sub = Path.Combine(link, "sub");
        Link(sub, elsewhere);

        using var repo = new LocalMeetingRepository(Path.Combine(_dir, "t2.db"), link);
        var media = Path.Combine(sub, "b.m4a");
        File.WriteAllBytes(media, new byte[] { 9 });
        Save(repo, "a", media);

        Assert.False(repo.IsManagedMedia(media));

        var result = repo.DeleteMeetings(new[] { "a" });

        // Unmanaged media never blocks the row; the file behind the below-root junction
        // is never touched, and the survivor is reported so the UI can say the recording
        // stayed (the confirm dialog promised it would be removed).
        Assert.Equal(new[] { "a" }, result.DeletedIds.ToArray());
        var skip = Assert.Single(result.SkippedMedia);
        Assert.Equal("a", skip.Id);
        Assert.Equal(media, skip.Path);
        Assert.True(File.Exists(Path.Combine(elsewhere, "b.m4a")));
        Assert.Null(repo.Load("a"));
    }
}

using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class BackendSyncRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mvb-repo-" + Guid.NewGuid().ToString("n"));

    public BackendSyncRepositoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private LocalMeetingRepository Open() => new(Path.Combine(_dir, "meetings.db"));

    [Fact]
    public void SaveLoad_RoundTrips_And_NullState_Unset()
    {
        using var repo = Open();
        Assert.Null(repo.LoadBackendSync("mv-1"));
        repo.SaveBackendSync(new BackendSyncRow("mv-1", "b1", "processing", null, true, "2026-10-12T00:00:00Z"));
        var row = repo.LoadBackendSync("mv-1")!;
        Assert.Equal("b1", row.BackendId);
        Assert.Equal("processing", row.State);
        Assert.True(row.Mock);
        repo.SaveBackendSync(row with { State = "ready", Mock = false, LastError = null });
        Assert.Equal("ready", repo.LoadBackendSync("mv-1")!.State);
    }

    [Fact]
    public void Reopen_Preserves_Row()
    {
        using (var repo = Open()) repo.SaveBackendSync(new BackendSyncRow("mv-2", "b2", "ready", null, false, "2026-10-12T00:00:00Z"));
        using var repo2 = Open();
        Assert.Equal("b2", repo2.LoadBackendSync("mv-2")!.BackendId);
    }

    [Fact]
    public void Clear_Removes_Row()
    {
        using var repo = Open();
        repo.SaveBackendSync(new BackendSyncRow("mv-3", "b3", "ready", null, false, "t"));
        repo.ClearBackendSync("mv-3");
        Assert.Null(repo.LoadBackendSync("mv-3"));
    }
}

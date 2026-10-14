using MeetVault.Core;
using MeetVault.Core.Backend;

namespace MeetVault.Core.Tests;

public class BackendAnalysisStateTests
{
    private static BackendStatus Status(string meetStatus, Dictionary<string, string> steps, bool mock = false)
        => new(meetStatus, steps, 0.5, null, null, new List<string>(), new List<string>(), mock, 0);

    [Fact]
    public void BothNull_IsNotAnalyzed()
        => Assert.Equal(AnalysisState.NotAnalyzed, BackendAnalysisState.Map(null, null, false));

    [Fact]
    public void SendingSync_IsUploading()
        => Assert.Equal(AnalysisState.Uploading,
            BackendAnalysisState.Map(Sync("sending"), null, false));

    [Fact]
    public void AnalyzeSucceededWithFreshDocs_IsCompleted()
        => Assert.Equal(AnalysisState.Completed,
            BackendAnalysisState.Map(Sync("ready"), Status("ready", new() { ["analyze"] = "succeeded" }), true));

    [Fact]
    public void AnalyzeSucceededWithoutDocs_IsNotCompleted()
        => Assert.Equal(AnalysisState.Queued,
            BackendAnalysisState.Map(null, Status("processing", new() { ["transcribe"] = "queued" }), false));

    [Fact]
    public void RunningStep_IsProcessing()
        => Assert.Equal(AnalysisState.Processing,
            BackendAnalysisState.Map(null, Status("processing", new() { ["transcribe"] = "running" }), false));

    [Fact]
    public void FailedMeeting_IsFailed()
        => Assert.Equal(AnalysisState.Failed,
            BackendAnalysisState.Map(null, Status("failed", new() { ["analyze"] = "failed" }), false));

    [Fact]
    public void FailedStatusWins_EvenWhenDocsExist()
        => Assert.Equal(AnalysisState.Failed,
            BackendAnalysisState.Map(Sync("ready"), Status("failed", new() { ["analyze"] = "failed" }), true));

    [Fact]
    public void SyncOnlyMapping()
    {
        Assert.Equal(AnalysisState.Processing, BackendAnalysisState.Map(Sync("processing"), null, false));
        Assert.Equal(AnalysisState.Completed, BackendAnalysisState.Map(Sync("ready"), null, false));
        Assert.Equal(AnalysisState.Failed, BackendAnalysisState.Map(Sync("failed"), null, false));
        Assert.Equal(AnalysisState.Failed, BackendAnalysisState.Map(Sync("not_found"), null, false));
    }

    [Theory]
    [InlineData("ready", "analyze", "succeeded", true, AnalysisState.Completed)]
    [InlineData("processing", "analyze", "running", false, AnalysisState.Processing)]
    [InlineData("processing", "transcribe", "queued", false, AnalysisState.Queued)]
    [InlineData("failed", "analyze", "failed", false, AnalysisState.Failed)]
    [InlineData("uploaded", null, null, false, AnalysisState.NotAnalyzed)]
    public void CloudRowMapping(string meetStatus, string? jobType, string? jobState, bool hasAnalysis, AnalysisState expected)
        => Assert.Equal(expected, BackendAnalysisState.FromCloudRow(meetStatus, jobType, jobState, hasAnalysis));

    private static BackendSyncRow Sync(string state) => new("m1", "b1", state, null, false, "2026-10-14T00:00:00Z");

    private static BackendBundle Bundle(bool? runIsMock, bool bundleMock) => new(
        "m1", "t1",
        runIsMock is null ? null
            : new BackendAnalysisRun("r1", "t1", "opencode-go", "deepseek-v4.1-flash", "p1", runIsMock.Value, "succeeded", null),
        null, new List<BackendAgendaItem>(), new List<BackendSection>(), new List<BackendSectionSummary>(),
        new List<BackendDecision>(), new List<BackendActionItem>(), new List<BackendQaItem>(), new List<string>(), bundleMock);

    [Fact]
    public void ShowMockBadge_NoAnalysis_Hides()
        => Assert.False(BackendAnalysisState.ShowMockBadge(null));

    [Fact]
    public void ShowMockBadge_RealRunOverMockPipeline_Hides()
        => Assert.False(BackendAnalysisState.ShowMockBadge(Bundle(runIsMock: false, bundleMock: true)));

    [Fact]
    public void ShowMockBadge_MockRun_Shows()
        => Assert.True(BackendAnalysisState.ShowMockBadge(Bundle(runIsMock: true, bundleMock: false)));

    [Fact]
    public void ShowMockBadge_NoRunBundleMock_Shows()
        => Assert.True(BackendAnalysisState.ShowMockBadge(Bundle(runIsMock: null, bundleMock: true)));

    [Fact]
    public void ShowMockBadge_NoRunBundleReal_Hides()
        => Assert.False(BackendAnalysisState.ShowMockBadge(Bundle(runIsMock: null, bundleMock: false)));
}

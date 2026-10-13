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
}

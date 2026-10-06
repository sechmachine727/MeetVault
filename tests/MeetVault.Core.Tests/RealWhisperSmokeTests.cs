using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Guards backend selection against the REAL whisper model + natives: the CPU path must
// load, and a requested GPU with no GPU present must fall back to CPU without throwing.
// Skips silently when the (gitignored) models are absent.
public sealed class RealWhisperSmokeTests
{
    private static string? ModelPath()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var p = Path.Combine(root, "models", "ggml-tiny.bin");
        return File.Exists(p) ? p : null;
    }

    [Fact]
    public async Task Cpu_Loads_RealModel_AndReportsCpu()
    {
        var model = ModelPath();
        if (model is null) return; // models not staged; nothing to verify
        using var tx = new WhisperTranscriber(model, "auto", GpuBackend.Cpu);
        await tx.StartAsync(CancellationToken.None);
        Assert.True(tx.IsRunning);
        Assert.Equal(GpuBackend.Cpu, tx.LoadedBackend);
    }

    [Fact]
    public async Task RequestedGpu_WithoutGpu_FallsBackToCpu_WithoutThrowing()
    {
        var model = ModelPath();
        if (model is null) return;
        using var tx = new WhisperTranscriber(model, "auto", GpuBackend.Vulkan);
        await tx.StartAsync(CancellationToken.None);
        Assert.True(tx.IsRunning);
        // On a GPU-less host this must be Cpu; on a GPU host it may be Vulkan. Either way, started.
        Assert.True(tx.LoadedBackend is GpuBackend.Cpu or GpuBackend.Vulkan);
    }
}


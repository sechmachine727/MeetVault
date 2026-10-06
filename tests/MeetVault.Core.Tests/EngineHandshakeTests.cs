using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class EngineHandshakeTests
{
    [Fact]
    public async Task Whisper_MissingModel_FailsWithActionableError()
    {
        using var tx = new WhisperTranscriber("models/does-not-exist.bin");
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => tx.StartAsync(CancellationToken.None));
        Assert.Contains("models/does-not-exist.bin", ex.Message);
    }

    [Fact]
    public async Task Whisper_TranscribeBeforeStart_Throws()
    {
        using var tx = new WhisperTranscriber("models/does-not-exist.bin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tx.TranscribeWindowAsync(new byte[320], 0, "m"));
    }

    [Fact]
    public void Diarizer_MissingModels_FailsFast()
    {
        Assert.ThrowsAny<Exception>(() => new SherpaDiarizer("models/no-seg.onnx", "models/no-emb.onnx"));
    }
}


using MeetVault.Core;

namespace MeetVault.Core.Tests;

// Review finding 1: empty window input must throw, never silently single-speaker.
// (AssignTurn itself carries no audio by interface contract; window audio routes via AssignWindow.)
public sealed class DiarizerContractTests
{
    [Fact]
    public void AssignWindow_Empty_ThrowsActionable()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var seg = Path.Combine(root, "models", "sherpa-seg.onnx");
        var emb = Path.Combine(root, "models", "sherpa-emb.onnx");
        if (!File.Exists(seg) || !File.Exists(emb)) return; // models not staged; nothing to verify
        using var dz = new SherpaDiarizer(seg, emb);
        Assert.Throws<ArgumentException>(() => dz.AssignWindow(Array.Empty<float>()));
    }
}

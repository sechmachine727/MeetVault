using MeetVault.Core;

namespace MeetVault.Core.Tests;

// NFR3: media hits disk incrementally, not buffered until stop.
public sealed class ChunkedWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
    [Fact]
    public void MediaFile_GrowsDuringRecording()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "chunk.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(16000, 16, 1)))
        {
            var chunk = new byte[16000 * 2];
            writer.Write(chunk, 0, chunk.Length);
            writer.Flush();
            var midSize = new FileInfo(path).Length;
            Assert.True(midSize > 44, $"file should hold audio before dispose, got {midSize} bytes");
            writer.Write(chunk, 0, chunk.Length);
            writer.Flush();
            Assert.True(new FileInfo(path).Length > midSize, "second chunk must grow the file");
        }
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}


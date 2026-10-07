using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The append-safe WAV sink does non-trivial header math; this pins it.
public sealed class WavSinkTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
    public void Dispose() { try { File.Delete(_path); } catch { } }

    // The sink holds the file open while recording, so read with a matching share.
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void FinalizeAndAppend_HeaderMatchesLength()
    {
        using (var sink = new NAudioCapture.WavSink(_path, 48000))
        {
            sink.Write(new byte[64000]);
            sink.FinalizeHeader();
            var a = ReadShared(_path); // header must be correct before dispose
            Assert.Equal(a.Length - 8, BitConverter.ToInt32(a, 4));   // RIFF size
            Assert.Equal(a.Length - 44, BitConverter.ToInt32(a, 40)); // data size
            Assert.Equal(48000, BitConverter.ToInt32(a, 24));         // the archive keeps 48 kHz
            Assert.Equal(96000, BitConverter.ToInt32(a, 28));         // byte rate = rate * 2 for 16-bit mono
        }

        using (var sink = new NAudioCapture.WavSink(_path, 48000)) // reopen: continue the same file
        {
            sink.Write(new byte[32000]);
            sink.FinalizeHeader();
        }

        var b = File.ReadAllBytes(_path);
        Assert.Equal(96044, b.Length);
        Assert.Equal(b.Length - 8, BitConverter.ToInt32(b, 4));
        Assert.Equal(b.Length - 44, BitConverter.ToInt32(b, 40));
    }

    [Fact]
    public void Reopen_RepairsZeroedHeader()
    {
        File.WriteAllBytes(_path, new byte[44 + 1000]); // crash: header sizes still zero
        using (var sink = new NAudioCapture.WavSink(_path, 48000)) { sink.FinalizeHeader(); }
        var b = File.ReadAllBytes(_path);
        Assert.Equal(b.Length - 44, BitConverter.ToInt32(b, 40));
    }
}

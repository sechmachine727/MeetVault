using MeetVault.Core;
using NAudio.Wave;

namespace MeetVault.Core.Tests;

// The capture pipeline now stores a full-band archive and hands the transcriber a 16 kHz
// copy. Both halves are non-trivial and both were wrong before (16 kHz mono WAV files that
// sounded muffled), so they are pinned here instead of trusted.
public sealed class ArchiveAndDownmixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Tone48k(int samples)
    {
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 12000);
            bytes[2 * i] = (byte)v;
            bytes[2 * i + 1] = (byte)(v >> 8);
        }
        return bytes;
    }

    // A finished meeting keeps a playable recording at a fraction of the WAV size.
    [Fact]
    public void Compact_EncodesWav_KeepingDurationAndType()
    {
        Directory.CreateDirectory(_dir);
        var wav = Path.Combine(_dir, "meeting.wav");
        const int seconds = 2;
        using (var sink = new NAudioCapture.WavSink(wav, 48000)) sink.Write(Tone48k(48000 * seconds));
        var wavBytes = new FileInfo(wav).Length;

        var kept = NAudioCapture.Compact(wav);

        Assert.NotNull(kept);
        Assert.True(File.Exists(kept), "compacted file " + kept + " does not exist");
        Assert.Equal("audio/mp4", NAudioCapture.ArchiveMime(kept!));
        // Compact only encodes. The working WAV is deleted by the session afterwards, once the
        // row points at the .m4a, so a failed save cannot leave a row without its recording.
        Assert.True(File.Exists(wav), "Compact must leave the working WAV for the caller to delete");
        Assert.True(new FileInfo(kept!).Length < wavBytes, "the archive should be smaller than the WAV");
        using var reader = new MediaFoundationReader(kept);
        Assert.InRange(reader.TotalTime.TotalSeconds, seconds - 0.3, seconds + 0.3);
    }

    [Fact]
    public void Compact_MissingFile_IsUnchanged()
    {
        var missing = Path.Combine(_dir, "nothing.wav");
        Assert.Equal(missing, NAudioCapture.Compact(missing));
        Assert.Equal("audio/wav", NAudioCapture.ArchiveMime(missing));
    }

    // Whatever the sources are, the transcriber must receive one second of 16 kHz mono for
    // one second of archive audio. A drift here time-shifts the whole transcript.
    [Fact]
    public void InferenceDownmixer_Returns16kAtTheArchivePace()
    {
        const int slice = 480; // 10 ms at 48 kHz, the pump's read size
        var downmixer = new NAudioCapture.InferenceDownmixer();
        var archiveSlice = new float[slice];
        var output = new MemoryStream();
        for (var block = 0; block < 100; block++)
        {
            for (var i = 0; i < slice; i++)
            {
                var n = block * slice + i;
                archiveSlice[i] = (float)(Math.Sin(2 * Math.PI * 440 * n / 48000.0) * 0.5);
            }
            var pcm = downmixer.Push(archiveSlice, slice);
            output.Write(pcm, 0, pcm.Length);
        }

        var bytes = output.ToArray();
        Assert.Equal(16000, bytes.Length / 2);              // one second of 16 kHz mono
        Assert.Contains(bytes, b => b != 0);                // the tone survived, not silence
    }
}

namespace MeetVault.Core.Tests;

internal static class TestAudio
{
    public static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static float[] ReadWav16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pos = 12; var dataOff = -1; var dataLen = 0;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var len = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data") { dataOff = pos + 8; dataLen = len; break; }
            pos += 8 + len + (len & 1);
        }
        Assert.True(dataOff >= 0, "no data chunk in " + path);
        var f = new float[dataLen / 2];
        for (var i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt16(bytes, dataOff + 2 * i) / 32768f;
        return f;
    }
}

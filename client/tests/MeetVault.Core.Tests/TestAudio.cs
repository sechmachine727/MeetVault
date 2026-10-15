namespace MeetVault.Core.Tests;

internal static class TestAudio
{
    // Walk up to the solution root instead of counting '..' levels: the output path gains
    // an 'x64' segment under Platform=x64, which broke fixed-depth resolution.
    public static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MeetVault.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root (MeetVault.sln) not found above " + AppContext.BaseDirectory);
    }

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

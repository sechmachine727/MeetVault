using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The catalog is the single source of truth for the shipped speech models: Vosk small
// only (English, Japanese, Vietnamese), default English, legacy keys resolve cleanly.
public sealed class EngineCatalogTests
{
    [Fact]
    public void Options_AreVoskSmallOnly_DefaultEnglish()
    {
        Assert.Equal(3, EngineCatalog.Options.Count);
        Assert.Equal("small-en", EngineCatalog.Default.Model);
        Assert.Equal("en", EngineCatalog.Default.Language);
        Assert.Equal(new[] { "en", "ja", "vi" }, EngineCatalog.Options.Select(o => o.Language).ToArray());
        Assert.All(EngineCatalog.Options, o => Assert.StartsWith("Vosk small", o.Label));
        Assert.All(EngineCatalog.Options, o => Assert.Contains("vosk/" + o.Model + "/am/final.mdl", o.RequiredFiles));
    }

    [Fact]
    public void Resolve_ExactModel_Wins()
    {
        Assert.Equal("small-ja", EngineCatalog.Resolve("small-ja").Model);
        Assert.Equal("small-vi", EngineCatalog.Resolve(" SMALL-VI ").Model);
    }

    [Fact]
    public void Resolve_MissingOrLegacyModel_FallsBackToDefault()
    {
        Assert.Equal("small-en", EngineCatalog.Resolve(null).Model);
        Assert.Equal("small-en", EngineCatalog.Resolve("").Model);
        // Every model name from the retired engine families resolves to the default.
        foreach (var legacy in new[] { "tiny", "base", "tiny-en", "tiny-ja", "base-en", "base-ja", "20m-en" })
            Assert.Equal("small-en", EngineCatalog.Resolve(legacy).Model);
    }

    [Fact]
    public void IsAvailable_ChecksEveryRequiredFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        try
        {
            var option = EngineCatalog.Options[1]; // small-ja
            Assert.False(EngineCatalog.IsAvailable(option, dir)); // nothing staged
            foreach (var f in option.RequiredFiles)
            {
                var path = Path.Combine(dir, f);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "");
            }
            Assert.True(EngineCatalog.IsAvailable(option, dir));
            File.Delete(Path.Combine(dir, option.RequiredFiles[0]));
            Assert.False(EngineCatalog.IsAvailable(option, dir)); // one missing file is enough
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void IndexOf_MapsModelToRow_DefaultZero()
    {
        Assert.Equal(0, EngineCatalog.IndexOf("small-en"));
        Assert.Equal(1, EngineCatalog.IndexOf("small-ja"));
        Assert.Equal(2, EngineCatalog.IndexOf("small-vi"));
        Assert.Equal(0, EngineCatalog.IndexOf("base")); // legacy -> default row
    }
}

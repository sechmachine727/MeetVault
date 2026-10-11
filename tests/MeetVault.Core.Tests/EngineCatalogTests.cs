using MeetVault.Core;

namespace MeetVault.Core.Tests;

// One source of truth for selectable engines/models, shared by the Settings picker and
// LivePage.ResolveEngines. Pure resolution — no file IO beyond an injected root.
public sealed class EngineCatalogTests
{
    [Fact]
    public void Resolve_KnownPair_ReturnsThatOption()
    {
        var o = EngineCatalog.Resolve("zipformer", "20m-en");
        Assert.Equal("zipformer", o.Engine);
        Assert.True(o.Streaming);
    }

    [Fact]
    public void Resolve_MissingOrUnknownEngine_FallsBackToDefault()
    {
        Assert.Equal("whisper", EngineCatalog.Resolve(null, "tiny").Engine);
        Assert.Equal("whisper", EngineCatalog.Resolve("", "base").Engine);
        Assert.Equal("whisper", EngineCatalog.Resolve("banana", "tiny").Engine);
    }

    [Fact]
    public void Resolve_LegacyModelOnly_KeepsWhisperTinyBase()
    {
        Assert.Equal("tiny", EngineCatalog.Resolve(null, "tiny").Model);
        Assert.Equal("base", EngineCatalog.Resolve(null, "base").Model);
    }

    [Fact]
    public void Resolve_KnownEngineUnknownModel_TakesThatEnginesFirstOption()
    {
        Assert.Equal("tiny-en", EngineCatalog.Resolve("moonshine", "banana").Model);
    }

    [Fact]
    public void IsAvailable_ChecksEveryRequiredFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mv-catalog-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var o = EngineCatalog.Resolve("zipformer", "20m-en");
            Assert.False(EngineCatalog.IsAvailable(o, dir));
            foreach (var f in o.RequiredFiles)
            {
                var p = Path.Combine(dir, f);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "x");
            }
            Assert.True(EngineCatalog.IsAvailable(o, dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IndexOf_RoundTripsWithOptions()
    {
        for (var i = 0; i < EngineCatalog.Options.Count; i++)
        {
            var o = EngineCatalog.Options[i];
            Assert.Equal(i, EngineCatalog.IndexOf(o.Engine, o.Model));
        }
    }
}

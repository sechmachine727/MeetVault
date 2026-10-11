using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The catalog is the single source of truth for the shipped speech models: one Vosk
// family, a language × size matrix (English small/medium/large, Japanese small/large,
// Vietnamese small/large), default English/small, and a one-time migration from the
// retired single-key `transcription.model`.
public sealed class EngineCatalogTests
{
    [Fact]
    public void Options_CoverTheLanguageSizeMatrix()
    {
        Assert.Equal(new[] { "en", "en", "en", "ja", "ja", "vi", "vi" }, EngineCatalog.Options.Select(o => o.Language).ToArray());
        Assert.Equal(new[] { "small", "medium", "large", "small", "large", "small", "large" }, EngineCatalog.Options.Select(o => o.Size).ToArray());
        Assert.All(EngineCatalog.Options, o => Assert.Contains("vosk/" + o.Model + "/am/final.mdl", o.RequiredFiles));
        // Every required file is relative to the models root and lives under the model's own
        // folder — the graph entries must carry the same vosk/<model>/ prefix as the rest.
        // (A missing prefix still passes the Contains check above but breaks IsAvailable at
        // runtime: the catalog would probe models/graph/HCLr.fst, which never exists.)
        Assert.All(EngineCatalog.Options, o => Assert.All(o.RequiredFiles, f => Assert.StartsWith("vosk/" + o.Model + "/", f)));
        Assert.All(EngineCatalog.Options, o => Assert.True(o.DownloadMb > 0));
    }

    [Fact]
    public void Find_NeedsTheExactPair()
    {
        Assert.Equal("medium-en", EngineCatalog.Find("en", "medium")!.Model);
        Assert.Equal("large-vi", EngineCatalog.Find(" VI ", "LARGE")!.Model);
        Assert.Null(EngineCatalog.Find("ja", "medium"));   // no Japanese medium model exists
        Assert.Null(EngineCatalog.Find("vi", "medium"));   // no Vietnamese medium model exists
        Assert.Null(EngineCatalog.Find("xx", "small"));
        Assert.Null(EngineCatalog.Find(null, null));
    }

    [Fact]
    public void Resolve_Exact_ThenSameLanguageSmall_ThenLegacy_ThenDefault()
    {
        Assert.Equal("medium-en", EngineCatalog.Resolve("en", "medium").Model);
        Assert.Equal("small-vi", EngineCatalog.Resolve("vi", "medium").Model);            // no vi medium -> vi small
        Assert.Equal("small-ja", EngineCatalog.Resolve(null, null, "small-ja").Model);    // retired single key
        Assert.Equal("medium-en", EngineCatalog.Resolve("", "", " MEDIUM-EN ").Model);
        Assert.Equal("small-en", EngineCatalog.Resolve(null, null, "base").Model);        // retired engine family
        Assert.Equal("small-en", EngineCatalog.Resolve("xx", "large").Model);
        Assert.Equal("small-en", EngineCatalog.Default.Model);
        Assert.Equal("en", EngineCatalog.Default.Language);
    }

    [Fact]
    public void MigrateLegacy_MapsStagedIds_ElseDefault()
    {
        Assert.Equal(("en", "small"), EngineCatalog.MigrateLegacy("small-en"));
        Assert.Equal(("en", "medium"), EngineCatalog.MigrateLegacy(" MEDIUM-EN "));
        Assert.Equal(("en", "large"), EngineCatalog.MigrateLegacy("large-en"));
        Assert.Equal(("ja", "small"), EngineCatalog.MigrateLegacy("small-ja"));
        Assert.Equal(("ja", "large"), EngineCatalog.MigrateLegacy("large-ja"));
        Assert.Equal(("vi", "small"), EngineCatalog.MigrateLegacy("small-vi"));
        Assert.Equal(("en", "small"), EngineCatalog.MigrateLegacy(null));
        Assert.Equal(("en", "small"), EngineCatalog.MigrateLegacy("base"));
    }

    [Fact]
    public void Labels_AreTierThenLanguage()
    {
        Assert.Equal("Vosk medium — English", EngineCatalog.Find("en", "medium")!.Label);
        Assert.Equal("Vosk large — Japanese", EngineCatalog.Find("ja", "large")!.Label);
    }

    [Fact]
    public void IsAvailable_ChecksEveryRequiredFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        try
        {
            var option = EngineCatalog.Find("en", "medium")!;
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
}

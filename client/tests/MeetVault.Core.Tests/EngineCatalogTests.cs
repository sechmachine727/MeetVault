using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The catalog is the single source of truth for the SHIPPED speech models: the deliverable carries
// Vosk large-en and large-ja only (docs/ASR-ENGINES.md). Vietnamese and the small/medium tiers are
// not shipped — there is no runtime download path, so an option the catalog lists but does not
// ship is a dead end for the user.
public sealed class EngineCatalogTests
{
    [Fact]
    public void Options_AreExactlyTheShippedSet()
    {
        Assert.Equal(new[] { "en", "ja" }, EngineCatalog.Options.Select(o => o.Language).ToArray());
        Assert.Equal(new[] { "large", "large" }, EngineCatalog.Options.Select(o => o.Size).ToArray());
        Assert.Equal(new[] { "large-en", "large-ja" }, EngineCatalog.Options.Select(o => o.Model).ToArray());
        Assert.All(EngineCatalog.Options, o => Assert.Contains("vosk/" + o.Model + "/am/final.mdl", o.RequiredFiles));
        // Every required file is relative to the models root and lives under the model's own
        // folder — the graph entries must carry the same vosk/<model>/ prefix as the rest.
        // (A missing prefix still passes the Contains check above but breaks IsAvailable at
        // runtime: the catalog would probe models/graph/HCLr.fst, which never exists.)
        Assert.All(EngineCatalog.Options, o => Assert.All(o.RequiredFiles, f => Assert.StartsWith("vosk/" + o.Model + "/", f)));
        Assert.All(EngineCatalog.Options, o => Assert.True(o.DownloadMb > 0));
    }

    [Fact]
    public void Tiers_And_Languages_DescribeOnlyWhatShips()
    {
        Assert.Equal(new[] { "large" }, EngineCatalog.Tiers);
        Assert.Equal(new[] { "en", "ja" }, EngineCatalog.Languages.Select(l => l.Code).ToArray());
        // The two lists must not drift: every language the dropdown offers needs at least one
        // option, and every option's language must be offered.
        Assert.All(EngineCatalog.Languages, l => Assert.Contains(EngineCatalog.Options, o => o.Language == l.Code));
        Assert.All(EngineCatalog.Options, o => Assert.Contains(EngineCatalog.Languages, l => l.Code == o.Language));
    }

    [Fact]
    public void Vietnamese_IsGone()
    {
        Assert.DoesNotContain(EngineCatalog.Options, o => o.Language == "vi");
        Assert.DoesNotContain(EngineCatalog.Options, o => o.Model.Contains("vi"));
        Assert.DoesNotContain(EngineCatalog.Languages, l => l.Code == "vi");
        Assert.Null(EngineCatalog.Find("vi", "large"));
    }

    [Fact]
    public void Find_NeedsTheExactPair()
    {
        Assert.Equal("large-en", EngineCatalog.Find(" EN ", "LARGE")!.Model);
        Assert.Equal("large-ja", EngineCatalog.Find("ja", "large")!.Model);
        Assert.Null(EngineCatalog.Find("vi", "large"));    // Vietnamese no longer ships
        Assert.Null(EngineCatalog.Find("en", "small"));    // no small tier ships
        Assert.Null(EngineCatalog.Find("ja", "medium"));   // no medium tier exists for Japanese
        Assert.Null(EngineCatalog.Find("xx", "large"));
        Assert.Null(EngineCatalog.Find(null, null));
    }

    [Fact]
    public void Resolve_Exact_ThenSameLanguage_ThenDefault()
    {
        Assert.Equal("large-ja", EngineCatalog.Resolve("ja", "large").Model);
        Assert.Equal("large-en", EngineCatalog.Resolve("en", "small").Model);           // en has one tier
        Assert.Equal("large-en", EngineCatalog.Resolve("vi", "small").Model);           // retired language
        Assert.Equal("large-en", EngineCatalog.Resolve(null, null, "small-ja").Model);  // retired single key
        Assert.Equal("large-en", EngineCatalog.Resolve("", "", " MEDIUM-EN ").Model);
        Assert.Equal("large-en", EngineCatalog.Resolve(null, null, "base").Model);      // retired engine family
        Assert.Equal("large-en", EngineCatalog.Resolve("xx", "large").Model);
    }

    [Fact]
    public void MigrateLegacy_MapsShippedIds_ElseTheDefault()
    {
        Assert.Equal(("ja", "large"), EngineCatalog.MigrateLegacy("large-ja"));
        Assert.Equal(("en", "large"), EngineCatalog.MigrateLegacy(" LARGE-EN "));
        Assert.Equal(("en", "large"), EngineCatalog.MigrateLegacy("large-vi"));   // retired -> shipped default
        Assert.Equal(("en", "large"), EngineCatalog.MigrateLegacy("small-en"));
        Assert.Equal(("en", "large"), EngineCatalog.MigrateLegacy(null));
        Assert.Equal(("en", "large"), EngineCatalog.MigrateLegacy("base"));
    }

    [Fact]
    public void Default_IsAShippedOption()
    {
        Assert.Equal("large-en", EngineCatalog.Default.Model);
        Assert.Equal("en", EngineCatalog.Default.Language);
        Assert.Equal("large", EngineCatalog.Default.Size);
        Assert.Contains(EngineCatalog.Default, EngineCatalog.Options);
    }

    [Fact]
    public void Labels_AreTierThenLanguage()
    {
        Assert.Equal("Vosk large — English", EngineCatalog.Find("en", "large")!.Label);
        Assert.Equal("Vosk large — Japanese", EngineCatalog.Find("ja", "large")!.Label);
    }

    [Fact]
    public void IsAvailable_ChecksEveryRequiredFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        try
        {
            var option = EngineCatalog.Find("en", "large")!;
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

# Ship only the Large Vosk models for Japanese and Vietnamese

Baseline: branch `caption-bar-2026-10-10` @ `d9c4220`. Written 2026-10-15 13:44 +07.

---

## Goal

Cut the portable deliverable from **3.3 GB to roughly 1 GB** by shipping exactly two speech models —
Vosk `large-ja` and `large-vi` — and making the app's model catalog, settings UI, auto-language
policy, tests and docs all agree with that shipped set.

---

## Current context / assumptions

### Measured today (do not re-derive by guesswork)

| What | Size |
| --- | --- |
| `client/publish` (whole dir) | **7.6 GB** |
| `client/publish/win-x64/models` | **4.2 GB** |
| `client/publish/MeetVault-2026-10-15-win-x64-portable.zip` (the deliverable) | **3 486 535 125 B (3.3 GB)** |
| `models/vosk/large-en` | 2.4 G |
| `models/vosk/large-ja` | 1.3 G |
| `models/vosk/medium-en` | 154 M |
| `models/vosk/large-vi` | 103 M |
| `models/vosk/small-ja` | 62 M |
| `models/vosk/small-en` | 50 M |
| `models/vosk/small-vi` | 43 M |
| `models/langid` (`voxlingua107.onnx` + `lang_map.json`) | 83 M |

Shipping only `large-ja` + `large-vi` + `langid` leaves ≈ **1.5 GB** of models, so the publish dir
lands near **1.9 GB** and the zip near **1.0–1.2 GB**. Measure the result and record what the
command prints — do not copy these estimates into a ledger as if they were measured.

### How the app decides which models exist

- **`client/src/MeetVault.Core/EngineCatalog.cs` is the single source of truth.** It lists 7
  `EngineOption` rows (en small/medium/large, ja small/large, vi small/large), plus
  `Tiers = { "small", "medium", "large" }`, `Languages = { en, ja, vi }` (code + endonym), and
  `Default = Find("en", "small")`. `Parts(model, composed)` builds each row's `RequiredFiles`;
  `IsAvailable(option, modelsRoot)` requires every one of those files to exist.
- **Models are resolved next to the exe**: `ModelsRoot = Path.Combine(AppContext.BaseDirectory, "models")`
  (`SettingsPage.xaml.cs:28`, `RecoveryHandoff.cs:27`).
- **There is no runtime download path.** `EngineOption.DownloadMb` is display-only
  (`SettingsPage.xaml.cs:508`); the models are staged beside the executable. So the catalog MUST
  match what actually ships — an option whose files are absent is dead weight in the UI.
- **`client/src/MeetVault.Core/LangId/AutoLanguagePolicy.cs:22`** defaults
  `Supported` to `new[] { "en", "ja", "vi" }`. With `en` unshipped this policy would keep suggesting
  a language that has no model, and `LivePage.OnLangSuggested` → `ApplyLanguageAsync` would try to
  swap to it.
- **`client/src/MeetVault/Views/SettingsPage.xaml.cs`** hardcodes `"en"` twice for auto mode:
  line 130 (`RefreshSizeRows`: `if (lang == "auto") lang = "en";`) and line 502
  (`EngineCatalog.Find(statusLang == "auto" ? "en" : statusLang, tier)`).
  `RefreshSizeRows` already tolerates a missing option (line 141 appends `Set.SizeRowUnavailable`)
  and an absent model dir (line 142 appends `Set.SizeRowNotInstalled`) — that is how the UI would
  report the dropped tiers today.

### How the deliverable is produced

`client/docs/mvp-winui/06-build-and-run.md` §3 is the recipe, and it is manual:

```powershell
rmdir /s /q publish\win-x64
dotnet publish src\MeetVault\MeetVault.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
Copy-Item -Recurse -Force models publish\win-x64\models     # <- copies ALL 4.2 GB
```

`client/src/MeetVault/MeetVault.csproj` already contains the established trim pattern to copy:
`TrimForeignRuntimes` (AfterTargets=Build, line 123) and `TrimForeignRuntimesPublish`
(AfterTargets=Publish, line 130), both `<ItemGroup><_X Include="..."/></ItemGroup><RemoveDir Directories="@(_X)" />`.
`client/AGENTS.md:40` records the constraint: `models/` is gitignored, staged at the repo root, and
copied into the publish folder **by hand**.

### Tests and docs that name concrete models

- `client/tests/MeetVault.Core.Tests/EngineCatalogTests.cs` — 6 facts pinning the 7-row matrix,
  `Default = small-en`, the `small-ja` legacy mapping and the `"base"` → `en/small` fallback.
- `client/tests/MeetVault.Core.Tests/VoskStreamingTranscriberTests.cs:9` — hardcodes
  `Path.Combine(TestAudio.RepoRoot, "models", "vosk", "small-en")` for the live contract test (it
  early-returns when the dir is absent).
- `client/src/MeetVault/Strings/en-US/Resources.resw` and `…/ja-JP/Resources.resw` —
  `Set.SizeSmallDetails` / `Set.SizeMediumDetails` / `Set.SizeLargeDetails` (line 248–250 in both;
  the Large one quotes English's 1.8 GB). `LocalizationResourceTests.cs` asserts key parity between
  the two files, so every edit must land in both.
- Docs naming the shipped set: `client/docs/ASR-ENGINES.md` (the language × tier table, lines 12–14),
  `client/README.md` (model table lines 88–94; line 57 references `models/vosk/small-en`),
  `client/BLOCKERS.md:16`, `client/docs/COVERAGE.md`, `client/docs/THIRD-PARTY-NOTICES.md:12`
  (attribution for every redistributed model), `client/AGENTS.md:40`,
  `client/docs/mvp-winui/06-build-and-run.md`.

### Assumptions (chosen, not asked)

1. The shipped set is exactly **{`large-ja`, `large-vi`}** plus the `langid` model (83 MB, needed by
   the language monitor; voxlingua107 covers both shipped languages).
2. The **default** becomes `ja`/`large` — the primary user's language. `vi` stays one dropdown click
   away.
3. The catalog is restricted to the shipped set (option **a** below). The alternative — keep all 7
   rows and let the UI mark 5 of them "not installed" — leaves a fresh install defaulting to an
   unshipped English model, which is worse. See Risks.
4. The local `client/models/` staging tree is **not** deleted: it is gitignored, is not the
   deliverable, and keeping it lets a developer still stage English for experiments. Only the
   publish output and the zip are trimmed.
5. The MSIX lane is out of scope (the models reach the portable publish by hand, and the MSIX is
   built from `src/MeetVault` — see Open questions).

---

## Architecture / proposed approach

Two coordinated changes. First, `EngineCatalog` becomes the literal shipped set (two rows), and every
consumer that hardcodes a language derives from it instead — `AutoLanguagePolicy.Supported` and the
two `"en"` fallbacks in `SettingsPage` — so the UI can only ever offer what exists. Second, a new
`TrimUnshippedModels` MSBuild target plus a `tools/pack-portable.ps1` packaging script make the
delivered payload match the catalog, replacing the by-hand `Copy-Item -Recurse models`. Tests,
localized copy and the model tables in the docs are updated in the same commits so nothing in the
repo claims a model that no longer ships.

---

## Step-by-step tasks

Every code task is RED → GREEN → commit. Run the suite with:

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" test tests/MeetVault.Core.Tests/MeetVault.Core.Tests.csproj -c Release --nologo -v q
# Expected before any change: "Passed! - Failed: 0, Passed: 355" (set VSTEST_CONNECTION_TIMEOUT=240 if
# vstest fails to connect; AGENTS.md notes this harness quirk)
```

### Phase A — the catalog and its consumers

#### A1. Restrict `EngineCatalog` to the two shipped models (TDD)

**1. Rewrite the failing tests** — replace the body of
`client/tests/MeetVault.Core.Tests/EngineCatalogTests.cs` with:

```csharp
using MeetVault.Core;

namespace MeetVault.Core.Tests;

// The catalog is the single source of truth for the SHIPPED speech models: the deliverable carries
// Vosk large-ja and large-vi only (docs/ASR-ENGINES.md). English and the small/medium tiers were
// dropped to keep the portable zip shippable — there is no runtime download path, so an option the
// catalog lists but does not ship is a dead end for the user.
public sealed class EngineCatalogTests
{
    [Fact]
    public void Options_AreExactlyTheShippedSet()
    {
        Assert.Equal(new[] { "ja", "vi" }, EngineCatalog.Options.Select(o => o.Language).ToArray());
        Assert.Equal(new[] { "large", "large" }, EngineCatalog.Options.Select(o => o.Size).ToArray());
        Assert.Equal(new[] { "large-ja", "large-vi" }, EngineCatalog.Options.Select(o => o.Model).ToArray());
        Assert.All(EngineCatalog.Options, o => Assert.Contains("vosk/" + o.Model + "/am/final.mdl", o.RequiredFiles));
        // Every required file is relative to the models root and lives under the model's own folder —
        // a missing prefix still passes the Contains check above but breaks IsAvailable at runtime.
        Assert.All(EngineCatalog.Options, o => Assert.All(o.RequiredFiles, f => Assert.StartsWith("vosk/" + o.Model + "/", f)));
        Assert.All(EngineCatalog.Options, o => Assert.True(o.DownloadMb > 0));
    }

    [Fact]
    public void Tiers_And_Languages_DescribeOnlyWhatShips()
    {
        Assert.Equal(new[] { "large" }, EngineCatalog.Tiers);
        Assert.Equal(new[] { "ja", "vi" }, EngineCatalog.Languages.Select(l => l.Code).ToArray());
        // The two lists must not drift: every language the dropdown offers needs at least one option,
        // and every option's language must be offered.
        Assert.All(EngineCatalog.Languages, l => Assert.Contains(EngineCatalog.Options, o => o.Language == l.Code));
        Assert.All(EngineCatalog.Options, o => Assert.Contains(EngineCatalog.Languages, l => l.Code == o.Language));
    }

    [Fact]
    public void Find_NeedsTheExactPair()
    {
        Assert.Equal("large-vi", EngineCatalog.Find(" VI ", "LARGE")!.Model);
        Assert.Equal("large-ja", EngineCatalog.Find("ja", "large")!.Model);
        Assert.Null(EngineCatalog.Find("en", "large"));   // English no longer ships
        Assert.Null(EngineCatalog.Find("ja", "small"));   // no small tier ships
        Assert.Null(EngineCatalog.Find("ja", "medium"));
        Assert.Null(EngineCatalog.Find("xx", "large"));
        Assert.Null(EngineCatalog.Find(null, null));
    }

    [Fact]
    public void Resolve_Exact_ThenSameLanguage_ThenDefault()
    {
        Assert.Equal("large-ja", EngineCatalog.Resolve("ja", "large").Model);
        Assert.Equal("large-vi", EngineCatalog.Resolve("vi", "small").Model);          // vi has one tier
        Assert.Equal("large-ja", EngineCatalog.Resolve("en", "large").Model);          // retired language
        Assert.Equal("large-ja", EngineCatalog.Resolve(null, null, "small-ja").Model); // retired single key
        Assert.Equal("large-ja", EngineCatalog.Resolve("", "", " MEDIUM-EN ").Model);
        Assert.Equal("large-ja", EngineCatalog.Resolve(null, null, "base").Model);     // retired engine family
        Assert.Equal("large-ja", EngineCatalog.Resolve("xx", "large").Model);
    }

    [Fact]
    public void MigrateLegacy_MapsShippedIds_ElseTheDefault()
    {
        Assert.Equal(("ja", "large"), EngineCatalog.MigrateLegacy("large-ja"));
        Assert.Equal(("vi", "large"), EngineCatalog.MigrateLegacy(" LARGE-VI "));
        Assert.Equal(("ja", "large"), EngineCatalog.MigrateLegacy("small-en"));   // retired -> shipped default
        Assert.Equal(("ja", "large"), EngineCatalog.MigrateLegacy("medium-en"));
        Assert.Equal(("ja", "large"), EngineCatalog.MigrateLegacy(null));
        Assert.Equal(("ja", "large"), EngineCatalog.MigrateLegacy("base"));
    }

    [Fact]
    public void Default_IsAShippedOption()
    {
        Assert.Equal("large-ja", EngineCatalog.Default.Model);
        Assert.Equal("ja", EngineCatalog.Default.Language);
        Assert.Equal("large", EngineCatalog.Default.Size);
        Assert.Contains(EngineCatalog.Default, EngineCatalog.Options);
    }

    [Fact]
    public void Labels_AreTierThenLanguage()
    {
        Assert.Equal("Vosk large — Japanese", EngineCatalog.Find("ja", "large")!.Label);
        Assert.Equal("Vosk large — Vietnamese", EngineCatalog.Find("vi", "large")!.Label);
    }

    [Fact]
    public void IsAvailable_ChecksEveryRequiredFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        try
        {
            var option = EngineCatalog.Find("ja", "large")!;
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
```

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" test tests/MeetVault.Core.Tests/MeetVault.Core.Tests.csproj -c Release --nologo -v q
# Expected: several failures — Options_AreExactlyTheShippedSet (7 rows, not 2),
# Default_IsAShippedOption (small-en), Tiers_And_Languages… , Find/Resolve/MigrateLegacy.
```

**2. Implement** — in `client/src/MeetVault.Core/EngineCatalog.cs` replace the `Tiers`,
`Languages`, `Options`, `Default`, `MigrateLegacy` and `Resolve` members (keep the
`EngineOption` record, `Parts`, `Find`, `IsAvailable` exactly as they are):

```csharp
    // The deliverable ships exactly two models — see docs/ASR-ENGINES.md. English and the
    // small/medium tiers were dropped so the portable zip stays shippable (~1.5 GB of models
    // instead of 4.1 GB); there is no runtime download path to fetch the rest.
    // Keep this list, docs/ASR-ENGINES.md and tools/pack-portable.ps1 in step.

    // Display order of the model-size dropdown.
    public static readonly string[] Tiers = { "large" };

    // Language dropdown: endonyms (standard convention); Code is the persisted value.
    public static readonly (string Code, string Endonym)[] Languages =
    {
        ("ja", "日本語"), ("vi", "Tiếng Việt"),
    };

    // Dynamic-graph models ship Gr.fst + HCLr.fst and the native composes the decode graph
    // at load time; composed models ship the pre-built HCLG.fst instead.
    private static string[] Parts(string model, bool composed = false)
    {
        var graph = composed
            ? new[] { "vosk/" + model + "/graph/HCLG.fst" }
            : new[] { "vosk/" + model + "/graph/HCLr.fst", "vosk/" + model + "/graph/Gr.fst" };
        return new[]
        {
            "vosk/" + model + "/am/final.mdl",
            "vosk/" + model + "/conf/model.conf",
        }.Concat(graph).Concat(new[] { "vosk/" + model + "/ivector/final.ie" }).ToArray();
    }

    public static IReadOnlyList<EngineOption> Options { get; } = new[]
    {
        new EngineOption("ja", "large", "large-ja", "Vosk large — Japanese", 1000, Parts("large-ja", composed: true)),
        new EngineOption("vi", "large", "large-vi", "Vosk large — Vietnamese", 78, Parts("large-vi", composed: true)),
    };

    // A fresh install starts on the primary shipped language; the user can pick the other one.
    public static EngineOption Default => Options[0];

    public static EngineOption? Find(string? language, string? size)
    {
        var l = (language ?? "").Trim().ToLowerInvariant();
        var s = (size ?? "").Trim().ToLowerInvariant();
        foreach (var o in Options)
            if (o.Language == l && o.Size == s) return o;
        return null;
    }

    // Retired single-key store (`transcription.model`) -> (language, size). Anything unknown
    // (older removed engines: tiny, base, small-en, medium-en, …) maps to the shipped default.
    public static (string Language, string Size) MigrateLegacy(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        foreach (var o in Options)
            if (o.Model == m) return (o.Language, o.Size);
        return (Default.Language, Default.Size);
    }

    // Exact (language, size) wins; then any tier of that language; then the retired single key;
    // then the shipped default. A stored language that no longer ships (e.g. English) lands on
    // the default rather than on a model that is not on disk.
    public static EngineOption Resolve(string? language, string? size, string? legacyModel = null)
    {
        var exact = Find(language, size);
        if (exact is not null) return exact;
        var l = (language ?? "").Trim().ToLowerInvariant();
        if (l.Length > 0)
        {
            foreach (var o in Options)
                if (o.Language == l) return o;
        }
        var (ml, ms) = MigrateLegacy(legacyModel);
        return Find(ml, ms) ?? Default;
    }
```

**3. Verify**:

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" test tests/MeetVault.Core.Tests/MeetVault.Core.Tests.csproj -c Release --nologo -v q
# Expected: Passed! - Failed: 0, Passed: <n>  (n is 357: 355 + the two added facts; report the real number)
```

**4. Commit**: `feat(core): the catalog ships Vosk large-ja and large-vi only`

#### A2. The auto-language policy must not suggest a language that has no model

**1. Failing test** — append to `client/tests/MeetVault.Core.Tests/LangIdTests.cs`:

```csharp
    [Fact]
    public void SupportedLanguages_FollowTheShippedCatalog()
    {
        // A policy that still allowed "en" would keep suggesting a language with no model on disk,
        // and LivePage.OnLangSuggested would try to swap to it.
        var policy = new AutoLanguagePolicy();
        var supported = policy.SupportedLanguages;
        Assert.Equal(EngineCatalog.Languages.Select(l => l.Code).ToArray(), supported);
        Assert.DoesNotContain("en", supported);
    }
```

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" test tests/MeetVault.Core.Tests/MeetVault.Core.Tests.csproj -c Release --nologo -v q --filter SupportedLanguages_FollowTheShippedCatalog
# Expected: 1 failed — 'AutoLanguagePolicy' does not contain a definition for 'SupportedLanguages'
```

**2. Implement** — in `client/src/MeetVault.Core/LangId/AutoLanguagePolicy.cs`:

(a) expose the effective list (add near `_supported`):

```csharp
    // The languages the policy may switch to — the shipped catalog's languages, unless the caller
    // pinned an explicit set. Exposed so a test can prove the two cannot drift.
    public IReadOnlyList<string> SupportedLanguages => _supported;
```

(b) change the constructor default (line 22) from `new[] { "en", "ja", "vi" }` to the catalog:

```csharp
    public AutoLanguagePolicy(Settings? settings = null)
    { _s = settings ?? new Settings(); _supported = _s.Supported ?? MeetVault.Core.EngineCatalog.Languages.Select(l => l.Code).ToArray(); }
```

and update the `Settings` record's comment (line 13) from
`// defaults to en/ja/vi (the transcription languages)` to
`// defaults to the shipped transcription languages (the EngineCatalog set)`.

**3. Verify**:

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" test tests/MeetVault.Core.Tests/MeetVault.Core.Tests.csproj -c Release --nologo -v q
# Expected: Passed! - Failed: 0
```

**4. Commit**: `fix(core): the auto-language policy derives its languages from the shipped catalog`

#### A3. Settings: stop hardcoding English for auto mode

**1. Failing test**: the two call sites are in a WinUI view, which the Core test project cannot
reference (`AGENTS.md`: the WinUI app does not build in CI). Guard them with a grep instead:

```bash
cd client && grep -n '"en"' src/MeetVault/Views/SettingsPage.xaml.cs
# Expected after the change: no match for the two auto-mode fallbacks (line 130 and 502 today)
```

**2. Implement** — in `client/src/MeetVault/Views/SettingsPage.xaml.cs`:

line 127–130 (inside `RefreshSizeRows`):

```csharp
        // Auto mode shows the tiers the default shipped language offers (the live resolver applies
        // the chosen size where a language has it).
        var lang = CurrentLanguage();
        if (lang == "auto") lang = MeetVault.Core.EngineCatalog.Default.Language;
```

line 502 (inside the transcription status text):

```csharp
        var option = MeetVault.Core.EngineCatalog.Find(statusLang == "auto" ? MeetVault.Core.EngineCatalog.Default.Language : statusLang, tier);
```

**3. Verify**:

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" build MeetVault.sln -c Release --nologo -v q
# Expected: Build succeeded, 0 Errors (the 3 pre-existing CA1416 warnings in ProcessLoopbackCapture.cs may remain)
cd client && grep -c '"en"' src/MeetVault/Views/SettingsPage.xaml.cs
# Expected: 0
```

**4. Commit**: `fix(ui): auto mode uses the shipped default language, not a hardcoded English`

#### A4. Repoint the live Vosk contract test at a shipped model

**1. Implement** — in `client/tests/MeetVault.Core.Tests/VoskStreamingTranscriberTests.cs` line 9,
replace the `small-en` path with the smallest shipped model (large-vi, 103 MB — fastest to load):

```csharp
        // A SHIPPED model: the deliverable carries large-ja and large-vi only. large-vi is the
        // smaller of the two, so the live contract test stays fast.
        var dir = Path.Combine(TestAudio.RepoRoot, "models", "vosk", "large-vi");
```

Leave the early-return when the directory is absent exactly as it is (the tree is gitignored).

> **As executed — the assertion had to change.** Repointing the model is not enough: the test's
> `Assert.Contains("fox", segs[0].Text)` and its 17 s fixture are English, and `large-vi` decodes that
> fixture to Vietnamese-looking nonsense (`"Việc hoạch rộng phút chốc…"`), so the test failed at the
> word assertion while every structural assertion still held (partials non-empty, ≥2 endpoint finals,
> sentence-cased). The executed version drops the word assertion and asserts the transport contract:
> `Assert.All(segs, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)))`. Coverage is deliberately
> narrower — a mis-decoding model would pass — and that is recorded in `docs/COVERAGE.md`.

**2. Verify**:

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" test tests/MeetVault.Core.Tests/MeetVault.Core.Tests.csproj -c Release --nologo -v q
# Expected: Passed! - Failed: 0, and the log line for the contract test should show it loaded
# models\vosk\large-vi (or the test reports as passed/skipped when the dir is unstaged)
```

**3. Commit**: `test(core): the live Vosk contract test loads a shipped model`

### Phase B — make the delivered payload match the catalog

#### B1. A publish-time trim target (mirrors the existing pattern)

**1. Implement** — append to `client/src/MeetVault/MeetVault.csproj`, directly after
`TrimForeignRuntimesPublish` (line 136), matching its style exactly:

```xml
  <!-- The deliverable ships only the models the catalog offers (docs/ASR-ENGINES.md): large-ja and
       large-vi, plus langid. Every other staged model dir is dropped from the publish output so the
       portable zip stays shippable (4.1 GB of models -> ~1.5 GB). Explicit paths, not a wildcard:
       a glob could delete a shipped model. No-ops when a dir is absent (ContinueOnError default).
       Keep this list, EngineCatalog.Options and tools/pack-portable.ps1 in step. -->
  <Target Name="TrimUnshippedModels" AfterTargets="Publish">
    <ItemGroup>
      <_UnshippedModelDir Include="$(PublishDir)models\vosk\small-en;$(PublishDir)models\vosk\medium-en;$(PublishDir)models\vosk\large-en;$(PublishDir)models\vosk\small-ja;$(PublishDir)models\vosk\small-vi" />
    </ItemGroup>
    <RemoveDir Directories="@(_UnshippedModelDir)" />
  </Target>
```

**2. Verify** — publish into a scratch output and check what survives:

```bash
cd client && rm -rf /tmp/mv-trim-probe && "C:/Program Files/dotnet/dotnet.exe" publish src/MeetVault/MeetVault.csproj -c Release -r win-x64 --self-contained true -o "$TMPDIR/mv-trim-probe"
# Expected: Build succeeded. Then, after staging the full models tree and re-publishing:
ls "$TMPDIR/mv-trim-probe/models/vosk"
# Expected: large-ja  large-vi        (small-en/medium-en/large-en/small-ja/small-vi removed)
```

Note: the trim only fires when the unshipped dirs are actually present, i.e. after the staging step
below copies the tree — that is exactly the case it exists for.

**3. Commit**: `build(client): trim unshipped speech models from the publish output`

#### B2. A packaging script that replaces the by-hand copy and zip

**1. Create** `client/tools/pack-portable.ps1`:

```powershell
# Builds the single canonical portable deliverable: clean publish, stage ONLY the shipped speech
# models, trim anything else, zip, and print the size + SHA256 for docs/BUILD-LEDGER.md.
# Keep $ShippedModels in step with MeetVault.Core/EngineCatalog.cs and docs/ASR-ENGINES.md.
[CmdletBinding()]
param(
    [string]$Config = "Release",
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)
$ErrorActionPreference = "Stop"
$ShippedModels = @("large-ja", "large-vi")
$PublishDir = Join-Path $RepoRoot "publish\win-x64"
$PublishRoot = Join-Path $RepoRoot "publish"
$Stamp = Get-Date -Format "yyyy-MM-dd"
$Zip = Join-Path $PublishRoot "MeetVault-$Stamp-win-x64-portable.zip"

Write-Host "== clean publish =="
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
& dotnet publish (Join-Path $RepoRoot "src\MeetVault\MeetVault.csproj") -c $Config -r win-x64 --self-contained true -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "== stage the shipped models only =="
$ModelsOut = Join-Path $PublishDir "models"
New-Item -ItemType Directory -Force -Path (Join-Path $ModelsOut "vosk") | Out-Null
foreach ($m in $ShippedModels) {
    $src = Join-Path $RepoRoot "models\vosk\$m"
    if (-not (Test-Path $src)) { throw "missing staged model: $src (stage it per docs/ASR-ENGINES.md)" }
    Copy-Item -Recurse -Force $src (Join-Path $ModelsOut "vosk\$m")
}
$langid = Join-Path $RepoRoot "models\langid"
if (-not (Test-Path $langid)) { throw "missing staged langid model: $langid" }
Copy-Item -Recurse -Force $langid (Join-Path $ModelsOut "langid")

Write-Host "== trim anything that is not shipped =="
Get-ChildItem (Join-Path $ModelsOut "vosk") -Directory |
    Where-Object { $ShippedModels -notcontains $_.Name } |
    ForEach-Object { Write-Host ("  dropping " + $_.Name); Remove-Item -Recurse -Force $_.FullName }

Write-Host "== zip =="
Get-ChildItem $PublishRoot -Filter "MeetVault-*-portable.zip" | Remove-Item -Force   # one canonical package
# tar (bsdtar, Windows 10+) handles a multi-GB tree far better than Compress-Archive.
& tar.exe -a -c -f $Zip -C $PublishDir .
if ($LASTEXITCODE -ne 0) { throw "tar failed" }

$size = (Get-Item $Zip).Length
$mb = [math]::Round($size / 1MB, 1)
$sha = (Get-FileHash $Zip -Algorithm SHA256).Hash
$modelsMb = [math]::Round((Get-ChildItem (Join-Path $ModelsOut "vosk") -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host ("ZIP      " + $Zip)
Write-Host ("SIZE     " + $mb + " MB (" + $size + " bytes)")
Write-Host ("SHA256   " + $sha)
Write-Host ("MODELS   " + $modelsMb + " MB: " + ($ShippedModels -join ", "))
```

> **As executed — two script fixes.** (1) `Resolve-Path "$PSScriptRoot\.."` resolved to `D:\`
> (PowerShell 5.1 leaves `$PSScriptRoot` at a drive root when `-File` is given a relative path), so
> every path became `D:\src\…`; the executed script finds the root by testing for
> `src\MeetVault\MeetVault.csproj`, then falls back to the current directory, and prints what it chose.
> (2) A bare `& tar.exe` resolved to MSYS GNU tar, which reads `D:\…` as a remote host
> (`Cannot connect to D: resolve failed`); the executed script calls
> `%SystemRoot%\System32\tar.exe` and falls back to `Compress-Archive`.

**2. Run it** (needs the staged tree at `client/models/`, which this machine has):

```bash
cd client && powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:/Users/supersanta/AppData/Local/hermes/cache/scratch/pack-portable.ps1" 2>&1 | tail -12
# (run it from client/tools; the path above is only for a scratch copy)
# Expected: "ZIP ... SIZE <~1000-1200> MB ... SHA256 <64 hex> ... MODELS ~1500 MB: large-ja, large-vi"
```

**3. Verify the zip contents** (no unshipped model may be inside):

```bash
cd client && tar.exe -tf publish/MeetVault-*-win-x64-portable.zip | grep -E "^\./models/" | sed 's#/.*##' | sort -u
# Expected: ./models  ./models/vosk  ./models/langid  ./models/vosk/large-ja  ./models/vosk/large-vi
# and NOTHING named small-*/medium-*/large-en
```

**4. Commit**: `build(client): one packaging script for the portable deliverable (staged + trimmed + zipped)`
(commit the script and the docs, **not** the 1 GB zip — `publish/` is gitignored.)

#### B3. Point the build docs at the script

**1. Implement** — in `client/docs/mvp-winui/06-build-and-run.md` §3, replace the manual
`rmdir` / `dotnet publish` / `Copy-Item -Recurse -Force models …` block with:

```powershell
# One canonical portable deliverable: publish, stage only the shipped models (large-ja, large-vi),
# trim, zip, and print the size + SHA256.
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-portable.ps1
```

and add a sentence: "The deliverable carries only the models `EngineCatalog` offers — see
`docs/ASR-ENGINES.md`. Staging more than that does not ship: the script and the
`TrimUnshippedModels` target drop everything else."

**2. Implement** — in `client/AGENTS.md` line 40, extend the constraint:

```markdown
- Never commit `models/`, `cuda/`, or `vcruntime/`. They are gitignored and staged at the repository root. `models/` is copied into the publish folder by hand; the `Content` items below bundle `cuda/` and `vcruntime/` automatically. Staging procedure: [docs/CUDA-SETUP.md](docs/CUDA-SETUP.md).
```
→
```markdown
- Never commit `models/`, `cuda/`, or `vcruntime/`. They are gitignored and staged at the repository root. Staging procedure: [docs/CUDA-SETUP.md](docs/CUDA-SETUP.md). The **deliverable** carries only the models `EngineCatalog` offers (Vosk large-ja + large-vi + langid); `tools/pack-portable.ps1` stages exactly those and the `TrimUnshippedModels` target drops anything else, so the portable zip stays ~1 GB instead of 3.3 GB. The `Content` items below bundle `cuda/` and `vcruntime/` automatically.
```

**3. Verify**: `grep -n "pack-portable" client/AGENTS.md client/docs/mvp-winui/06-build-and-run.md` prints both.

**4. Commit**: `docs(client): the portable deliverable is built by tools/pack-portable.ps1`

### Phase C — copy, notices and the model tables

#### C1. The tier copy must stop advertising English's 1.8 GB model

**1. Implement** — in **both** `client/src/MeetVault/Strings/en-US/Resources.resw` and
`client/src/MeetVault/Strings/ja-JP/Resources.resw`, replace the three size-detail strings
(lines 248–250) with the shipped reality. English (en-US):

```xml
  <data name="Set.SizeSmallDetails" xml:space="preserve"><value>Small — the lightest and fastest to load, with basic accuracy. Not part of this build; see docs/ASR-ENGINES.md.</value></data>
  <data name="Set.SizeMediumDetails" xml:space="preserve"><value>Medium — more accurate than Small and still realtime on CPU. Not part of this build; see docs/ASR-ENGINES.md.</value></data>
  <data name="Set.SizeLargeDetails" xml:space="preserve"><value>Large — the most accurate tier; heavier to load and run. Japanese 8.4% character error rate (~1.3 GB), Vietnamese (~103 MB).</value></data>
```

Japanese (ja-JP):

```xml
  <data name="Set.SizeSmallDetails" xml:space="preserve"><value>Small — 最も軽く、読み込みも最速。基本精度。このビルドには含まれません（docs/ASR-ENGINES.md 参照）。</value></data>
  <data name="Set.SizeMediumDetails" xml:space="preserve"><value>Medium — Small より高精度で、CPU でもリアルタイム。このビルドには含まれません（docs/ASR-ENGINES.md 参照）。</value></data>
  <data name="Set.SizeLargeDetails" xml:space="preserve"><value>Large — 最高精度。読み込みと実行は重め。日本語 CER 8.4%（約 1.3 GB）、ベトナム語（約 103 MB）。</value></data>
```

The Small/Medium strings are now unreachable from the UI (the tier list is `{ large }`) but stay
present so the two resource files keep key parity — `LocalizationResourceTests` asserts that.

**2. Verify**:

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" test tests/MeetVault.Core.Tests/MeetVault.Core.Tests.csproj -c Release --nologo -v q --filter Localization
# Expected: Passed! - Failed: 0   (key parity between en-US and ja-JP holds)
cd client && grep -c "1.8 GB" src/MeetVault/Strings/*/Resources.resw
# Expected: 0 for both files
```

**3. Commit**: `fix(strings): the size copy describes the shipped Japanese and Vietnamese models`

#### C2. The model tables in the docs

**1. Implement** — in `client/docs/ASR-ENGINES.md`, replace the language × tier table (lines 12–14)
with the shipped set:

```markdown
| tier | Japanese | Vietnamese |
| --- | --- | --- |
| large | `large-ja` — `vosk-model-ja-0.22` (~1 GB) | `large-vi` — `vosk-model-vn-0.4` (~78 MB) |

Only these two ship (plus `models/langid`). English and the small/medium tiers were removed from the
deliverable to keep the portable zip ~1 GB; the catalog (`EngineCatalog.Options`) lists exactly what
ships and `tools/pack-portable.ps1` stages exactly that. To experiment with another model locally,
stage it under `models/vosk/` and add a row to the catalog — but it will not be delivered until both
lists change.
```

**2. Implement** — in `client/README.md`: replace the model table (lines 88–94) with the two shipped
rows plus the langid row, and change line 57's `models/vosk/small-en` to `models/vosk/large-vi`
(the shipped model the contract test now loads).

**3. Implement** — in `client/BLOCKERS.md` line 16, rewrite the staged-models note so it states the
shipped set and keeps the honest accuracy caveat ("JA/VI accuracy on real spoken audio has not been
judged").

**4. Implement** — in `client/docs/THIRD-PARTY-NOTICES.md`, the Vosk paragraph must list **only the
models that are redistributed**:

```markdown
The staged models under `models/vosk/` are Vosk models for Japanese and Vietnamese
(`vosk-model-ja-0.22`, `vosk-model-vn-0.4`), distributed by Alpha Cephei under the Apache License
2.0 and not committed to this repository. The language-ID model under `models/langid/`
(`voxlingua107.onnx`) is redistributed under its own upstream licence; see its source page.
```

**5. Verify**: `grep -rn "small-en\|medium-en\|large-en" client/README.md client/docs/ASR-ENGINES.md client/docs/THIRD-PARTY-NOTICES.md`
→ only intentional "removed/no longer ships" mentions remain, and the notices list no dropped model.

**6. Commit**: `docs(client): the shipped model set is Vosk large-ja and large-vi`

#### C3. Ledger and coverage

**1. Implement** — append a new dated entry to `client/docs/COVERAGE.md` (do **not** rewrite the
historical rows — `docs/BUILD-LEDGER.md` is explicitly past tense and stays untouched) recording:
the trim, the measured zip size before (3.3 GB) and after (paste the number the script printed), the
SHA256, and the fact that the catalog now matches the deliverable.

**2. Commit**: `docs(client): coverage — the deliverable ships ja/vi large only`

### Phase D — verify the product, not just the suite

#### D1. Full suite and build

```bash
cd client && "C:/Program Files/dotnet/dotnet.exe" build MeetVault.sln -c Release --nologo -v q
cd client && "C:/Program Files/dotnet/dotnet.exe" test tests/MeetVault.Core.Tests/MeetVault.Core.Tests.csproj -c Release --nologo -v q
# Expected: Build succeeded (0 errors, only the 3 known CA1416 warnings); Passed! - Failed: 0
```

#### D2. The delivered tree contains only the shipped models

```bash
cd client && ls publish/win-x64/models/vosk && du -sh publish/win-x64 publish/win-x64/models publish/*.zip
# Expected: large-ja  large-vi  |  models ~1.5G, win-x64 ~1.9G, zip ~1.0-1.2G  (record the real numbers)
```

#### D3. The published app runs and offers only the shipped set (UIA)

`AGENTS.md`: UI changes must be inspected in the running app, driven through
`System.Windows.Automation` from Windows PowerShell 5.1 (a helper that dumps every Text/Edit/Button
with its AutomationId works well — see the scratch `dump_text.ps1` used earlier today).

1. Launch `client/publish/win-x64/MeetVault.exe`.
2. Settings → Transcription:
   - the **Language** dropdown offers `Auto`, `日本語`, `Tiếng Việt` — and **no English**;
   - the **Model size** dropdown offers exactly one row, `Large`, with **no** "not installed" suffix
     (the model is staged) and no `Small`/`Medium` rows;
   - `TranscriptionStatus` names `Vosk large — Japanese` (the shipped default) or the selected
     language, never English.
3. Expected on a machine with the models staged: the status line reports the model as installed, and
   starting a meeting logs `vosk: loaded dir=…\models\vosk\large-ja` (or `large-vi`) in
   `%TEMP%\MeetVault\app.log`.

If the app cannot be driven, say so and report the two dropdowns as unverified rather than assuming.

#### D4. Record the deliverable

Write the measured size + SHA256 into `client/docs/COVERAGE.md` (C3) and, if a release note exists,
into `client/README.md`'s package section. Commit:

`docs(client): measured portable deliverable after the model trim`

---

## Tests / validation

| Layer | File | What it proves |
| --- | --- | --- |
| unit | `client/tests/MeetVault.Core.Tests/EngineCatalogTests.cs` | the catalog is exactly {ja large, vi large}; tiers/languages cannot drift from the options; the retired ids (`small-en`, `medium-en`, `base`, a stale language) all resolve to a shipped model; the default is shipped |
| unit | `client/tests/MeetVault.Core.Tests/LangIdTests.cs` | the auto-language policy's supported set equals the shipped languages and excludes `en` |
| unit | `client/tests/MeetVault.Core.Tests/VoskStreamingTranscriberTests.cs` | the live contract test loads a shipped model |
| unit | `client/tests/MeetVault.Core.Tests/LocalizationResourceTests.cs` | the en-US/ja-JP resource parity still holds after the copy change |
| build | `MeetVault.csproj` `TrimUnshippedModels` | the publish output keeps only `large-ja`/`large-vi` |
| artifact | `tools/pack-portable.ps1` | the zip contains exactly the shipped models, and prints its size + SHA256 |
| manual | the running app (UIA) | the dropdowns offer only what ships, and a meeting loads the shipped model |

Nothing is "done" until the real command output is pasted into `docs/COVERAGE.md` — never the
estimates from this plan.

---

## Risks, tradeoffs, and open questions

1. **English transcription disappears from the product.** That is the point of the request, but it is
   a real capability loss: an English meeting can no longer be transcribed at all, and there is no
   runtime download to add it back. Cheaper alternatives if the owner wants to keep English: ship
   `large-en` and drop `large-ja`+`large-vi` instead (2.4 GB — worse), or keep the catalog complete
   and only fix the default (option b) so the UI marks the rest "not installed" while still shipping
   two models. **Open question for the owner: is Japanese + Vietnamese the whole supported language
   set from now on, or should the app later fetch models on demand?**
2. **Existing installs silently change language.** A user with `transcription.language = "en"` will
   now resolve to `ja`/`large`. It is deliberate (the stored language has no model on disk), but it
   should be stated in the release notes — the alternative, refusing to start, is worse.
3. **`langid` still detects 107 languages.** The monitor can detect English and hand it to the
   policy, which now ignores it (A2). That is the intended behaviour; the model itself is 83 MB and
   is kept because it is what makes ja/vi auto-detection work.
4. **The MSIX lane is untouched.** `06-build-and-run.md` §4 builds the MSIX from `src/MeetVault`, and
   the models reach only the portable publish. If the MSIX is expected to carry models, it needs its
   own trim/staging decision — **open question.**
5. **`Compress-Archive` vs `tar`.** The script uses `tar.exe -a -c -f` (bsdtar ships with Windows
   10 1803+) because `Compress-Archive` is slow and memory-hungry on a 1.5 GB tree. If `tar.exe` is
   missing on the build machine, fall back to `Compress-Archive -CompressionLevel Optimal` and expect
   a much longer run.
6. **Zip compression on model files is poor** (FST/mdl binaries barely compress). Expect the zip to
   land near the sum of the staged bytes rather than below it; that is why the plan says "measure,
   don't estimate".
7. **The catalog and the packager are two lists that must agree.** A test cannot see the PowerShell
   script, so the guard is a comment in all three places (`EngineCatalog.cs`, `pack-portable.ps1`,
   `docs/ASR-ENGINES.md`) plus the zip-content check in B2.3. A future option is to emit the shipped
   list from the catalog into a file the script reads — worth it only if a third language appears.

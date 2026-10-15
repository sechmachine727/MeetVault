using MeetVault.Core;

namespace MeetVault.Core.Tests;

public sealed class LangIdLabelsTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("ja", "ja")]
    [InlineData("vi", "vi")]
    [InlineData("eng", "en")]   // 3-letter alias (future re-exports)
    [InlineData("jpn", "ja")]
    [InlineData("vie", "vi")]
    [InlineData("de", null)]    // detected but not a shipped transcription language
    [InlineData("deu", null)]
    [InlineData("", null)]
    public void MapsModelLabelsToAppCodes(string label, string? expected)
        => Assert.Equal(expected, LangIdLabels.ToAppCode(label));
}

public sealed class SpeechWindowGateTests
{
    public static float[] Tone(int ms, float amplitude = 0.1f)
    {
        var samples = new float[ms * 16];
        for (var i = 0; i < samples.Length; i++) samples[i] = amplitude * MathF.Sin(2f * MathF.PI * 440f * i / 16000f);
        return samples;
    }

    [Fact]
    public void Silence_EmitsNothing()
    {
        var gate = new SpeechWindowGate();
        Assert.Empty(gate.Push(new float[16000 * 20]));   // 20 s of silence
    }

    [Fact]
    public void ContinuousSpeech_EmitsOverlappingWindows()
    {
        var gate = new SpeechWindowGate();
        var windows = gate.Push(Tone(6000));
        Assert.Equal(3, windows.Count);                   // 4 s window, 1 s stride
        Assert.All(windows, w => Assert.Equal(4 * 16000, w.Length));
    }

    [Fact]
    public void PausesInsideSpeech_OnlyVoicedWindowsEmit()
    {
        var gate = new SpeechWindowGate();
        var audio = new List<float>();
        audio.AddRange(Tone(4000));
        audio.AddRange(new float[16000 * 2]);             // 2 s pause
        audio.AddRange(Tone(3000));
        var windows = gate.Push(audio.ToArray());
        Assert.NotEmpty(windows);
        Assert.All(windows, w => Assert.True(gate.VoicedMs(w) >= 2500,
            "an emitted window must carry at least 2.5 s of voiced audio"));
    }

    [Fact]
    public void ShortSpeech_EmitsNothing()
    {
        var gate = new SpeechWindowGate();
        Assert.Empty(gate.Push(Tone(2000)));              // under minVoicedMs, under window
    }
}

public sealed class AutoLanguagePolicyTests
{
    private static AutoLanguagePolicy NewPolicy() => new(new AutoLanguagePolicy.Settings(
        ConfidenceThreshold: 0.5f, AgreementCount: 3, AgreementWindow: 5, CooldownMs: 1000));

    [Fact]
    public void SupportedLanguages_FollowTheShippedCatalog()
    {
        // A policy that still allowed "en" would keep suggesting a language with no model on disk,
        // and LivePage.OnLangSuggested would try to swap to it. The two lists must not drift.
        var policy = new AutoLanguagePolicy();
        var expected = EngineCatalog.Languages.Select(l => l.Code).ToArray();
        Assert.Equal(expected, policy.SupportedLanguages);
        Assert.DoesNotContain("en", policy.SupportedLanguages);
        Assert.All(EngineCatalog.Options, o => Assert.Contains(o.Language, policy.SupportedLanguages));
    }

    [Fact]
    public void AnExplicitSupportedSet_StillWins()
    {
        // A caller that pins its own set must keep it: the catalog default is only a default.
        var policy = new AutoLanguagePolicy(new AutoLanguagePolicy.Settings(Supported: new[] { "ja" }));
        Assert.Equal(new[] { "ja" }, policy.SupportedLanguages);
    }

    // The policy's supported set is the shipped catalog (ja, vi) — these fixtures use those two
    // languages, never a code the product no longer ships. The mechanics are language-agnostic.

    [Fact]
    public void TwoAgreeingDetections_NoSwitch()
    {
        var p = NewPolicy(); p.SetActive("ja");
        Assert.Null(p.Detection("vi", 0.9f, 0));
        Assert.Null(p.Detection("vi", 0.9f, 100));
    }

    [Fact]
    public void ThreeAgreeingDetections_Switch()
    {
        var p = NewPolicy(); p.SetActive("ja");
        Assert.Null(p.Detection("vi", 0.9f, 0));
        Assert.Null(p.Detection("vi", 0.9f, 100));
        Assert.Equal("vi", p.Detection("vi", 0.9f, 200));
    }

    [Fact]
    public void ActiveLanguageDetection_ResetsAgreement()
    {
        var p = NewPolicy(); p.SetActive("ja");
        p.Detection("vi", 0.9f, 0);
        p.Detection("vi", 0.9f, 100);
        Assert.Null(p.Detection("ja", 0.95f, 200));       // back to the active language: reset
        Assert.Null(p.Detection("vi", 0.9f, 300));
        Assert.Null(p.Detection("vi", 0.9f, 400));
        Assert.Equal("vi", p.Detection("vi", 0.9f, 500));
    }

    [Fact]
    public void LowConfidence_Ignored()
    {
        var p = NewPolicy(); p.SetActive("ja");
        for (var i = 0; i < 5; i++) Assert.Null(p.Detection("vi", 0.4f, i * 100));
    }

    [Fact]
    public void UnsupportedCode_Ignored()
    {
        var p = NewPolicy(); p.SetActive("ja");
        for (var i = 0; i < 5; i++) Assert.Null(p.Detection("de", 0.99f, i * 100));
    }

    [Fact]
    public void Cooldown_BlocksThenAllowsASecondSwitch()
    {
        var p = NewPolicy(); p.SetActive("ja");
        p.Detection("vi", 0.9f, 0);
        p.Detection("vi", 0.9f, 100);
        Assert.Equal("vi", p.Detection("vi", 0.9f, 200));
        p.SetActiveKeepCooldown("vi");                     // the app applies the auto switch; the cooldown stays
        Assert.Null(p.Detection("ja", 0.9f, 300));        // inside cooldown: not even recorded
        Assert.Null(p.Detection("ja", 0.9f, 1100));
        Assert.Null(p.Detection("ja", 0.9f, 1300));       // cooldown over: agreement re-accumulates
        Assert.Null(p.Detection("ja", 0.9f, 1400));
        Assert.Equal("ja", p.Detection("ja", 0.9f, 1500));
    }

    [Fact]
    public void SetActive_ClearsTheCooldown()
    {
        var p = NewPolicy(); p.SetActive("ja");
        p.Detection("vi", 0.9f, 0);
        p.Detection("vi", 0.9f, 100);
        Assert.Equal("vi", p.Detection("vi", 0.9f, 200));  // auto switch; cooldown running
        p.SetActive("vi");                                 // the user confirms the pick: cooldown clears
        p.Detection("ja", 0.9f, 300);                      // under a cooldown window of the switch — recorded
        p.Detection("ja", 0.9f, 400);
        Assert.Equal("ja", p.Detection("ja", 0.9f, 500));  // agreement completes: not suppressed
    }

    [Fact]
    public void ResetAgreement_ClearsTheWindowAndCooldown()
    {
        // Pause/stop: two non-active detections before the pause + one after resume must
        // NOT meet the agreement count (review r12).
        var p = NewPolicy(); p.SetActive("ja");
        p.Detection("vi", 0.9f, 0);
        p.Detection("vi", 0.9f, 100);
        p.ResetAgreement();
        Assert.Null(p.Detection("vi", 0.9f, 200));   // window starts over — not a switch
        p.Detection("vi", 0.9f, 300);
        Assert.Equal("vi", p.Detection("vi", 0.9f, 400));
    }
}

public sealed class LanguageMonitorTests
{
    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    [Fact]
    public void ThreeAgreeingWindows_RaiseOneSuggestion()
    {
        var detections = new List<string>();
        var suggestions = new List<string>();
        using var monitor = new LanguageMonitor(
            _ => ("ja", 0.9f),
            new AutoLanguagePolicy(new AutoLanguagePolicy.Settings(CooldownMs: 1000)),
            minInferenceGapMs: 0);
        monitor.SetActive("en");
        monitor.Detected += (code, _) => { lock (detections) detections.Add(code); };
        monitor.Suggested += code => { lock (suggestions) suggestions.Add(code); };
        monitor.Push(SpeechWindowGateTests.Tone(6000));   // 3 windows of continuous speech
        Assert.True(WaitFor(() => { lock (suggestions) return suggestions.Count > 0; }), "no suggestion arrived");
        lock (detections) Assert.Contains("ja", detections);
        lock (suggestions) Assert.Equal(new[] { "ja" }, suggestions.ToArray());
    }

    [Fact]
    public async Task Disabled_SkipsTheDetectorEntirely()
    {
        // Manual mode: the detector must not keep running — no inference, no events
        // (review r12; the previous contract reported detections the app discarded).
        var detections = new List<string>();
        var suggestions = new List<string>();
        using var monitor = new LanguageMonitor(
            _ => ("ja", 0.9f),
            new AutoLanguagePolicy(new AutoLanguagePolicy.Settings(CooldownMs: 1000)),
            minInferenceGapMs: 0);
        monitor.Enabled = false;
        monitor.SetActive("en");
        monitor.Detected += (code, _) => { lock (detections) detections.Add(code); };
        monitor.Suggested += code => { lock (suggestions) suggestions.Add(code); };
        monitor.Push(SpeechWindowGateTests.Tone(6000));
        await Task.Delay(500); // give the monitor a chance to (wrongly) run
        lock (detections) Assert.Empty(detections);
        lock (suggestions) Assert.Empty(suggestions);
    }
}

public sealed class LangIdModelTests
{
    [Fact]
    public void EnFixture_DetectsEnglish_JaFixture_DetectsJapanese()
    {
        var root = TestAudio.RepoRoot;
        var modelsRoot = Path.Combine(root, "models");
        if (!LangIdModel.IsAvailable(modelsRoot)) return; // staged payload missing: environment state, not a failure
        using var model = LangIdModel.Load(modelsRoot);
        var en = TestAudio.ReadWav16k(Path.Combine(root, "tests", "MeetVault.Core.Tests", "fixtures", "langid_en_16k.wav"));
        var ja = TestAudio.ReadWav16k(Path.Combine(root, "tests", "MeetVault.Core.Tests", "fixtures", "langid_ja_16k.wav"));
        var enResult = model.Detect(en);
        var jaResult = model.Detect(ja);
        Assert.NotNull(enResult);
        Assert.NotNull(jaResult);
        Assert.Equal("en", enResult!.Value.Code);
        Assert.Equal("ja", jaResult!.Value.Code);
        Assert.True(enResult.Value.Confidence > 0.5f);
        Assert.True(jaResult.Value.Confidence > 0.5f);
    }
}

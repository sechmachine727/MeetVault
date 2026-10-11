using System.Xml.Linq;

namespace MeetVault.Core.Tests;

// Guards the translation set: both languages carry identical keys, every x:Uid used in
// XAML has entries, and every Loc.T/Loc.Tf key used in code exists. File IO only.
public sealed class LocalizationResourceTests
{
    private static string Root => TestAudio.RepoRoot;

    private static Dictionary<string, string> Load(string lang)
    {
        var doc = XDocument.Load(Path.Combine(Root, "src", "MeetVault", "Strings", lang, "Resources.resw"));
        return doc.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => d.Element("value")?.Value ?? "");
    }

    [Fact]
    public void BothLanguages_HaveIdenticalKeys_NoEmptyValues()
    {
        var en = Load("en-US"); var ja = Load("ja-JP");
        Assert.Equal(en.Keys.OrderBy(k => k, StringComparer.Ordinal), ja.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(en, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), "empty en value: " + kv.Key));
        Assert.All(ja, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), "empty ja value: " + kv.Key));
    }

    [Fact]
    public void EveryXamlUid_HasEntries()
    {
        var en = Load("en-US");
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src", "MeetVault"), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "x:Uid=\"([^\"]+)\""))
            {
                var uid = m.Groups[1].Value;
                Assert.True(en.Keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal)),
                    Path.GetFileName(file) + ": no resw entries for x:Uid=" + uid);
            }
        }
    }

    [Fact]
    public void EveryLocCall_HasAnEntry()
    {
        var en = Load("en-US");
        var callStart = new System.Text.RegularExpressions.Regex("Loc\\.Tf?\\(");
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src", "MeetVault"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match call in callStart.Matches(text))
            {
                // The call's argument region, paren-balanced (Tf arguments can nest calls).
                var i = call.Index + call.Length;
                var depth = 1;
                var end = i;
                while (end < text.Length && depth > 0)
                {
                    if (text[end] == '(') depth++;
                    else if (text[end] == ')') depth--;
                    if (depth == 0) break;
                    end++;
                }
                var region = text[i..end];
                // Key literals: the FIRST literal, plus switch/ternary arms (after => ? :)
                // that look like keys. Literal switch INPUTS (before =>) and plain value
                // arguments are not keys.
                foreach (System.Text.RegularExpressions.Match lit in System.Text.RegularExpressions.Regex.Matches(region, "\"([^\"]+)\""))
                {
                    var after = region[(lit.Index + lit.Length)..].TrimStart();
                    var before = region[..lit.Index].TrimEnd();
                    var isFirst = lit.Index == region.IndexOf('"');
                    var precededByArm = before.EndsWith("=>") || before.EndsWith("?") || before.EndsWith(":");
                    var looksLikeKey = System.Text.RegularExpressions.Regex.IsMatch(lit.Groups[1].Value, "^[A-Za-z][A-Za-z0-9]*\\.[A-Za-z0-9.]+$");
                    var isKey = !after.StartsWith("=>") && (isFirst || (precededByArm && looksLikeKey));
                    if (isKey)
                        Assert.True(en.ContainsKey(lit.Groups[1].Value), Path.GetFileName(file) + ": Loc key missing from resw: " + lit.Groups[1].Value);
                }
            }
        }
    }

    [Fact]
    public void Placeholders_HaveParityAcrossLanguages()
    {
        var en = Load("en-US"); var ja = Load("ja-JP");
        foreach (var (key, enValue) in en)
        {
            if (!ja.TryGetValue(key, out var jaValue)) continue; // key parity is guarded by BothLanguages_HaveIdenticalKeys_NoEmptyValues
            var enTokens = PlaceholderTokens(enValue);
            var jaTokens = PlaceholderTokens(jaValue);
            Assert.True(enTokens.SequenceEqual(jaTokens),
                key + ": placeholder mismatch — en=[" + string.Join(", ", enTokens) + "] ja=[" + string.Join(", ", jaTokens) + "]");
        }
    }

    // Ordered {n} tokens of a composite-format value. {{ and }} are escaped literal braces,
    // not placeholders, so strip the escapes first (otherwise "{{0}}" would parse as token 0).
    private static List<string> PlaceholderTokens(string value)
    {
        var normalized = value.Replace("{{", "").Replace("}}", "");
        return System.Text.RegularExpressions.Regex.Matches(normalized, "\\{(\\d+)\\}")
            .Select(m => m.Groups[1].Value).ToList();
    }
}

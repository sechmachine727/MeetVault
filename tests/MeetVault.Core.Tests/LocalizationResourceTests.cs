using System.Xml.Linq;

namespace MeetVault.Core.Tests;

// Guards the translation set: both languages carry identical keys, every x:Uid used in
// XAML has entries, and every Loc.T/Loc.Tf key used in code exists. File IO only.
public sealed class LocalizationResourceTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

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
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src", "MeetVault"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "Loc\\.Tf?\\(\"([^\"]+)\""))
                Assert.True(en.ContainsKey(m.Groups[1].Value), Path.GetFileName(file) + ": Loc key missing from resw: " + m.Groups[1].Value);
        }
    }
}

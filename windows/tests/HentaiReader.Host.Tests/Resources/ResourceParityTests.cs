using System.Xml.Linq;
using HentaiReader.Host.Tests.Parity;

namespace HentaiReader.Host.Tests.Resources;

/// <summary>
/// Every UI language carries the same strings as en-US (Constitution IX). Plural families are compared
/// by base key; each language must provide exactly the CLDR categories its plural rules use. English,
/// the default resource language, holds every category any language uses so that no key lacks a
/// default (MRT warning PRI263); it only ever selects one and other.
/// </summary>
public class ResourceParityTests
{
    private static readonly string[] PluralSuffixes = ["_zero", "_one", "_two", "_few", "_many", "_other"];

    private static readonly Dictionary<string, string[]> Categories = new()
    {
        ["en-US"] = ["one", "few", "many", "other"],
        ["de-DE"] = ["one", "other"],
        ["fr-FR"] = ["one", "many", "other"],
        ["es-ES"] = ["one", "many", "other"],
        ["ja-JP"] = ["other"],
        ["uk-UA"] = ["one", "few", "many", "other"],
    };

    public static TheoryData<string> Languages() => new(Categories.Keys.Where(l => l != "en-US"));

    [Theory]
    [MemberData(nameof(Languages))]
    public void SameKeysAsEnglish(string language)
    {
        var english = Split(Load("en-US"));
        var translated = Split(Load(language), english.Families.Keys);

        Assert.Equal(english.Plain.Order(), translated.Plain.Order());
        Assert.Equal(english.Families.Keys.Order(), translated.Families.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    [InlineData("en-US")]
    public void NoEmptyValues(string language)
    {
        var empty = Load(language).Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ToList();
        Assert.Empty(empty);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    [InlineData("en-US")]
    public void PluralFamiliesMatchLanguageRules(string language)
    {
        var families = Split(Load("en-US")).Families.Keys;
        foreach (var (family, categories) in Split(Load(language), families).Families)
        {
            Assert.Contains("other", categories);
            Assert.True(Categories[language].Order().SequenceEqual(categories.Order()),
                $"{language} {family}: [{string.Join(", ", categories)}]");
        }
    }

    private static Dictionary<string, string> Load(string language)
    {
        var path = Path.Combine(RepoPaths.Root, "windows", "src", "HentaiReader.App", "Strings", language, "Resources.resw");
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
    }

    /// <summary>
    /// Splits keys into plain strings and plural families. English defines the families (a base with both
    /// <c>_one</c> and <c>_other</c>), so a plain key like <c>Namespace_other</c> is never mistaken for one.
    /// </summary>
    private static (List<string> Plain, Dictionary<string, List<string>> Families) Split(
        Dictionary<string, string> strings, IEnumerable<string>? knownFamilies = null)
    {
        var known = knownFamilies?.ToHashSet() ?? strings.Keys
            .Where(k => k.EndsWith("_one") && strings.ContainsKey(k[..^4] + "_other"))
            .Select(k => k[..^4]).ToHashSet();
        var plain = new List<string>();
        var families = new Dictionary<string, List<string>>();
        foreach (var key in strings.Keys)
        {
            var suffix = PluralSuffixes.FirstOrDefault(s => key.EndsWith(s) && known.Contains(key[..^s.Length]));
            if (suffix is null)
            {
                plain.Add(key);
                continue;
            }
            var family = key[..^suffix.Length];
            if (!families.TryGetValue(family, out var list)) families[family] = list = [];
            list.Add(suffix[1..]);
        }
        return (plain, families);
    }
}

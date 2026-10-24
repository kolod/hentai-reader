using System.Text.RegularExpressions;
using HentaiReader.Host.Tests.Parity;

namespace HentaiReader.Host.Tests.Resources;

/// <summary>
/// The installer's own texts (windows/installer/Messages/&lt;lang&gt;.isl) exist in all six languages
/// with the same keys and placeholders (Constitution IX, spec FR-026).
/// </summary>
public partial class InstallerMessagesTests
{
    private static readonly string[] Others = ["de", "fr", "es", "ja", "uk"];

    private static string Folder => Path.Combine(RepoPaths.Root, "windows", "installer", "Messages");

    public static TheoryData<string> Languages() => new(Others);

    [Theory]
    [MemberData(nameof(Languages))]
    public void SameKeysAsEnglish(string language)
    {
        Assert.Equal(Load("en").Keys.Order(), Load(language).Keys.Order());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    [InlineData("en")]
    public void NoEmptyValues(string language)
    {
        Assert.Empty(Load(language).Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void SamePlaceholders(string language)
    {
        var english = Load("en");
        foreach (var (key, value) in Load(language))
        {
            Assert.True(Placeholders(english[key]).SequenceEqual(Placeholders(value)), $"{language} {key}: placeholders differ from English");
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TranslatedUnlessListed(string language)
    {
        var allowed = File.ReadAllLines(Path.Combine(Folder, "identical-translations.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();
        var english = Load("en");
        var untranslated = Load(language).Where(e => e.Value == english[e.Key] && !allowed.Contains($"{e.Key}={language}")).Select(e => e.Key);
        Assert.Empty(untranslated);
    }

    private static Dictionary<string, string> Load(string language)
    {
        var values = new Dictionary<string, string>();
        var inSection = false;
        foreach (var raw in File.ReadAllLines(Path.Combine(Folder, $"{language}.isl")))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[CustomMessages]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            var eq = line.IndexOf('=');
            Assert.True(eq > 0, $"{language}.isl: not a key=value line: {line}");
            Assert.True(values.TryAdd(line[..eq], line[(eq + 1)..]), $"{language}.isl: duplicate key {line[..eq]}");
        }
        return values;
    }

    private static IEnumerable<string> Placeholders(string value) => PlaceholderPattern().Matches(value).Select(m => m.Value).Order();

    [GeneratedRegex("%[0-9n]")]
    private static partial Regex PlaceholderPattern();
}

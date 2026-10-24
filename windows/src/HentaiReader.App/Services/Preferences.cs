using System.Text.Json;

namespace HentaiReader.App.Services;

/// <summary>App preferences (data-model.md): stored as JSON in the app data folder.</summary>
public sealed class Preferences
{
    public static readonly string[] Languages = ["en-US", "de-DE", "fr-FR", "es-ES", "ja-JP", "uk-UA"];

    /// <summary>"" = follow the system language.</summary>
    public string Language { get; set; } = "";
    public bool AdultConfirmed { get; set; }
    public string? LastSourceId { get; set; }

    private static string FilePath => Path.Combine(AppPaths.Data, "preferences.json");

    public static Preferences Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Data);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>The UI language to apply: the stored choice, else the first supported system language, else English.</summary>
    public string EffectiveLanguage()
    {
        if (Languages.Contains(Language)) return Language;
        // The preferred-language list can be empty; the Windows display language still counts.
        var system = Windows.System.UserProfile.GlobalizationPreferences.Languages
            .Append(System.Globalization.CultureInfo.InstalledUICulture.Name);
        foreach (var tag in system)
        {
            var match = Languages.FirstOrDefault(l => l.Equals(tag, StringComparison.OrdinalIgnoreCase))
                        ?? Languages.FirstOrDefault(l => l.Split('-')[0].Equals(tag.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return "en-US";
    }
}

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\HentaiReader, or HENTAIREADER_DATA (test runs, portable setups).</summary>
    public static string Data { get; } = Environment.GetEnvironmentVariable("HENTAIREADER_DATA") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HentaiReader");
    public static string SourceData(string id) => Path.Combine(Data, "sources", id);
}

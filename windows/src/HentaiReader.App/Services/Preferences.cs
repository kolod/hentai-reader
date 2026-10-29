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

    /// <summary>Pages kept loaded ahead of the view (3–5, FR-008).</summary>
    public int PreloadPages { get; set; } = 4;
    /// <summary>Maximum reading column width in DIP (600–2000, FR-011).</summary>
    public int ReadingWidth { get; set; } = 1000;
    /// <summary>Pages fill the window width when the reader opens (otherwise the reading width), FR-011.</summary>
    public bool FitWidth { get; set; } = true;
    /// <summary>Gap between pages in DIP (0–48, 0 = never); webtoons start without it, FR-004b.</summary>
    public int PageGap { get; set; } = 8;
    /// <summary>Disk cache limit for page images in MB (256–10240, FR-023).</summary>
    public int CacheLimitMb { get; set; } = 1024;
    /// <summary>
    /// History may fetch a missing cover through the source (getGallery, then its cover image) when
    /// neither a saved copy nor the page cache has the first page. Off: History makes no requests.
    /// </summary>
    public bool NetworkCovers { get; set; }
    public const string StartLastSource = "last-source", StartHistory = "history", StartLibrary = "library", StartSourcePrefix = "source:";

    /// <summary>Screen shown at start (FR-021): last-source, source:&lt;id&gt;, history or library.</summary>
    public string StartPage { get; set; } = StartLastSource;
    /// <summary>Folder of the page cache (null = %LOCALAPPDATA%\HentaiReader\cache\pages), FR-024.</summary>
    public string? CacheFolder { get; set; }
    /// <summary>The previous cache folder while a move runs (continues after a restart).</summary>
    public string? CacheMoveFrom { get; set; }
    /// <summary>Default search per source: query text and filter values (FR-022).</summary>
    public Dictionary<string, DefaultQuery> DefaultQueries { get; set; } = new();
    /// <summary>Main window bounds when it was last closed.</summary>
    public WindowBounds? Window { get; set; }
    /// <summary>Per-source setting values (sourceId → settingId → value), FR-026.</summary>
    public Dictionary<string, Dictionary<string, string>> SourceSettings { get; set; } = new();

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
    public static string Progress => Path.Combine(Data, "progress.json");
    public static string PageCache => Path.Combine(Data, "cache", "pages");
    /// <summary>Library database: folders, saved archives, history, progress, downloads (006).</summary>
    public static string Database => Path.Combine(Data, "library.db");
    /// <summary>Small cover thumbnails of history entries.</summary>
    public static string Covers => Path.Combine(Data, "covers");
    /// <summary>
    /// Library folder created on first use: Documents\Hentai Reader, or Library inside the data folder
    /// when HENTAIREADER_DATA is set (test runs, portable setups).
    /// </summary>
    public static string DefaultLibrary => Environment.GetEnvironmentVariable("HENTAIREADER_DATA") is { Length: > 0 }
        ? Path.Combine(Data, "Library")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Hentai Reader");
    /// <summary>Throw-away embedded browser profiles for sign-in (deleted after use).</summary>
    public static string WebView => Path.Combine(Data, "webview");
}

/// <summary>A source's saved default search (star button).</summary>
public sealed record DefaultQuery(string Text, System.Text.Json.Nodes.JsonObject Filters);

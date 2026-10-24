using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace HentaiReader.App.Localization;

/// <summary>
/// UI strings from Strings/&lt;tag&gt;/Resources.resw (English is the key source of truth and the
/// fallback), plural families `&lt;Key&gt;_&lt;category&gt;`, and labels for the contract vocabulary
/// (specs/003-windows-host/contracts/l10n-keys.md).
/// </summary>
public static class Strings
{
    private static readonly ResourceLoader Loader = new();

    /// <summary>The language the UI is shown in (BCP-47), set at startup.</summary>
    public static string Language { get; set; } = "en-US";

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);

    public static string Get(string key)
    {
        var value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    public static string Format(string key, params object[] args) => string.Format(Culture, Get(key), args);

    /// <summary>Plural form of `key` for `n`, formatted with `n` as {0}.</summary>
    public static string Plural(string key, long n)
    {
        var category = Localization.Plural.Category(Language, n);
        var value = Loader.GetString($"{key}_{category}");
        if (string.IsNullOrEmpty(value)) value = Loader.GetString($"{key}_other");
        return string.Format(Culture, value, n.ToString("N0", Culture));
    }

    public static string FilterLabel(string id) => Get($"Filter_{id}");
    public static string SortLabel(string value) => Get($"Sort_{value}");
    public static string TypeLabel(string value) => Get($"Type_{value}");
    public static string SettingLabel(string id) => Get($"Setting_{id}");
    /// <summary>Resource keys cannot hold '-', so "e-hentai" becomes "Setting_site_e_hentai".</summary>
    public static string SettingValueLabel(string id, string value) => Get($"Setting_{id}_{value.Replace('-', '_')}");
    public static string ErrorTitle(string kind) => Get($"Error_{kind}_Title");
    public static string ErrorBody(string kind) => Get($"Error_{kind}_Body");

    /// <summary>Known tag namespaces are translated; unknown ones are shown as the source gives them.</summary>
    public static string NamespaceLabel(string? ns)
    {
        if (string.IsNullOrEmpty(ns)) return Get("Namespace_other");
        var value = Loader.GetString($"Namespace_{ns}");
        return string.IsNullOrEmpty(value) ? ns : value;
    }

    /// <summary>Native name of an ISO 639-1 language ("日本語", "Deutsch").</summary>
    public static string LanguageName(string iso)
    {
        try
        {
            var native = CultureInfo.GetCultureInfo(iso).NativeName;
            return native.Length > 0 ? char.ToUpper(native[0], CultureInfo.GetCultureInfo(iso)) + native[1..] : iso;
        }
        catch (CultureNotFoundException)
        {
            return iso;
        }
    }

    public static string Date(DateTimeOffset date) => date.ToLocalTime().ToString("d", Culture);
    public static string Number(double value, string format = "N0") => value.ToString(format, Culture);
}

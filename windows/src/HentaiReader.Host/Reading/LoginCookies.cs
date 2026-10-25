using System.Text.Json.Nodes;
using HentaiReader.Host.Http;
using HentaiReader.Host.Sources;

namespace HentaiReader.Host.Reading;

/// <summary>
/// Which cookies of a sign-in a host keeps (contract 1.2 <c>manifest.loginPage</c>): only cookies
/// whose domain is within the source's domains; the account is shared, so the sign-in cookies found
/// on any of them are also stored for the login page's domain and every <c>shareWith</c> domain.
/// Also parses cookies the user pastes. Values are never logged.
/// </summary>
public static class LoginCookies
{
    /// <summary>Sign-in cookie names from loginPage.cookies that are not among <paramref name="cookies"/>.</summary>
    public static IReadOnlyList<string> Missing(JsonObject loginPage, IEnumerable<CookieJar.Cookie> cookies)
    {
        var present = cookies.Where(c => c.Value.Length > 0).Select(c => c.Name).ToHashSet();
        return Required(loginPage).Where(n => !present.Contains(n)).ToList();
    }

    /// <summary>The cookies to store for the source: within its domains; sign-in cookies for every account domain.</summary>
    public static List<CookieJar.Cookie> Select(JsonObject manifest, IEnumerable<CookieJar.Cookie> captured)
    {
        var domains = (manifest["domains"] as JsonArray ?? []).Select(d => d!.GetValue<string>()).ToList();
        var loginPage = manifest["loginPage"] as JsonObject;
        var kept = captured
            .Select(c => c with { Domain = c.Domain.TrimStart('.').ToLowerInvariant() })
            .Where(c => ManifestRules.HostInDomains(c.Domain, domains))
            .ToList();
        if (loginPage is null) return kept;

        var required = Required(loginPage);
        var account = kept.Where(c => required.Contains(c.Name)).GroupBy(c => c.Name).Select(g => g.First()).ToList();
        // Account domains: the login page's host and every shareWith domain.
        var accountDomains = (loginPage["shareWith"] as JsonArray ?? []).Select(d => d!.GetValue<string>().TrimStart('.').ToLowerInvariant()).ToList();
        if (loginPage["url"] is JsonValue url && Uri.TryCreate(url.GetValue<string>(), UriKind.Absolute, out var uri)) accountDomains.Insert(0, uri.Host.ToLowerInvariant());
        foreach (var domain in accountDomains.Distinct().Where(d => ManifestRules.HostInDomains(d, domains)))
        {
            kept.RemoveAll(c => c.Domain == domain && required.Contains(c.Name));
            kept.AddRange(account.Select(c => c with { Domain = domain, HostOnly = false, Path = "/" }));
        }
        return kept;
    }

    /// <summary>
    /// Cookies pasted as a Netscape cookies.txt or as <c>name=value; name=value</c> (then they belong to
    /// <paramref name="defaultDomain"/> and its subdomains).
    /// </summary>
    public static List<CookieJar.Cookie> Parse(string text, string defaultDomain)
    {
        var result = new List<CookieJar.Cookie>();
        // Any line ending: a WinUI TextBox uses \r alone, files use \r\n or \n.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        var netscape = lines.Any(l => l.Split('\t').Length >= 7);
        if (netscape)
        {
            foreach (var raw in lines)
            {
                var line = raw.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? raw["#HttpOnly_".Length..] : raw;
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var f = line.Split('\t');
                if (f.Length < 7 || f[5].Length == 0) continue;
                long.TryParse(f[4], out var expiry);
                result.Add(new CookieJar.Cookie(f[5], f[6].Trim(), f[0].TrimStart('.').ToLowerInvariant(),
                    !f[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase), f[2].Length > 0 ? f[2] : "/", expiry > 0 ? expiry * 1000 : null));
            }
            return result;
        }
        foreach (var part in text.Replace("\n", ";").Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Length > 0 && value.Length > 0) result.Add(new CookieJar.Cookie(name, value, defaultDomain.TrimStart('.').ToLowerInvariant(), false, "/", null));
        }
        return result;
    }

    private static HashSet<string> Required(JsonObject loginPage) =>
        (loginPage["cookies"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet();
}

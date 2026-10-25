using System.Text.Json.Nodes;
using HentaiReader.Host.Http;

namespace HentaiReader.Host.Sources;

/// <summary>
/// manifest.loginPage rules of contract 1.2 (specs/005-windows-reader/contracts/contract-1.2-login-page.md),
/// the same rules and texts as tools/runner/src/login-page.ts. A source that breaks them does not load.
/// </summary>
public static class ManifestRules
{
    /// <summary>True when <paramref name="host"/> is allowed by manifest domains ("site.org" or "*.site.org").</summary>
    public static bool HostInDomains(string host, IEnumerable<string> domains)
    {
        host = host.ToLowerInvariant();
        return domains.Any(d =>
        {
            var pattern = d.ToLowerInvariant();
            return pattern.StartsWith("*.", StringComparison.Ordinal)
                ? CookieJar.DomainMatches(host, pattern[2..]) && host != pattern[2..]
                : host == pattern;
        });
    }

    public static List<string> LoginPageViolations(JsonObject manifest)
    {
        var violations = new List<string>();
        if (manifest["loginPage"] is not JsonObject page) return violations;
        var domains = (manifest["domains"] as JsonArray ?? []).Select(d => d?.GetValue<string>() ?? "").ToList();

        var contract = manifest["contract"]?.GetValue<string>()?.Split('.');
        if (contract is not [var major, var minor] || !int.TryParse(major, out var mj) || !int.TryParse(minor, out var mn) || (mj == 1 && mn < 2))
            violations.Add("manifest.loginPage: requires contract 1.2");
        if (manifest["login"]?.GetValue<string>() == "none") violations.Add("manifest.loginPage: not allowed when login is \"none\"");

        var url = page["url"] is JsonValue u && u.TryGetValue<string>(out var text) && Uri.TryCreate(text, UriKind.Absolute, out var parsed) ? parsed : null;
        if (url is null || url.Scheme != Uri.UriSchemeHttps) violations.Add("manifest.loginPage.url: must be an absolute https URL");
        else if (!HostInDomains(url.Host, domains)) violations.Add($"manifest.loginPage.url: host {url.Host} is outside manifest.domains");

        var cookies = (page["cookies"] as JsonArray ?? []).Select(c => c is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList();
        if (cookies.Count == 0 || cookies.Any(string.IsNullOrEmpty) || cookies.Distinct().Count() != cookies.Count)
            violations.Add("manifest.loginPage.cookies: must list at least one cookie name, each once");

        foreach (var share in page["shareWith"] as JsonArray ?? [])
        {
            var domain = share is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            if (!HostInDomains(domain, domains)) violations.Add($"manifest.loginPage.shareWith: {domain} is outside manifest.domains");
        }
        return violations;
    }
}

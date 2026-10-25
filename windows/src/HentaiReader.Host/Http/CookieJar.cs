using System.Text.Json;

namespace HentaiReader.Host.Http;

/// <summary>
/// Per-source cookie jar: Set-Cookie parsing, domain/path matching, Netscape import, script-header
/// merge (port of tools/runner/src/host/cookies.ts).
/// </summary>
public sealed class CookieJar
{
    public sealed record Cookie(string Name, string Value, string Domain, bool HostOnly, string Path, long? Expires);

    private readonly object _lock = new();
    private List<Cookie> _cookies = new();

    /// <summary>Raised after any change, so a persistent store can save the jar.</summary>
    public event Action? Changed;

    public void Store(string url, IEnumerable<string> setCookieHeaders)
    {
        var u = new Uri(url);
        var changed = false;
        foreach (var header in setCookieHeaders)
        {
            var parts = header.Split(';');
            var eq = parts[0].IndexOf('=');
            if (eq <= 0) continue;
            var cookie = new Cookie(parts[0][..eq].Trim(), parts[0][(eq + 1)..].Trim(), u.Host.ToLowerInvariant(), true, "/", null);
            foreach (var attr in parts.Skip(1))
            {
                var kv = attr.Split('=', 2);
                var key = kv[0].Trim().ToLowerInvariant();
                var val = kv.Length > 1 ? kv[1].Trim() : "";
                if (key == "domain" && val.Length > 0)
                {
                    var domain = val.TrimStart('.').ToLowerInvariant();
                    if (!DomainMatches(u.Host.ToLowerInvariant(), domain)) continue;
                    cookie = cookie with { Domain = domain, HostOnly = false };
                }
                else if (key == "path" && val.StartsWith('/'))
                {
                    cookie = cookie with { Path = val };
                }
                else if (key == "max-age" && double.TryParse(val, out var seconds))
                {
                    cookie = cookie with { Expires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(seconds * 1000) };
                }
                else if (key == "expires" && cookie.Expires is null && DateTimeOffset.TryParse(val, out var at))
                {
                    cookie = cookie with { Expires = at.ToUnixTimeMilliseconds() };
                }
            }
            Put(cookie);
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Seeds "k=v; k2=v2" for a domain and its subdomains.</summary>
    public void Seed(string domain, string cookieString)
    {
        foreach (var part in cookieString.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            Put(new Cookie(part[..eq].Trim(), part[(eq + 1)..].Trim(), domain.TrimStart('.').ToLowerInvariant(), false, "/", null));
        }
        Changed?.Invoke();
    }

    /// <summary>Seeds from a Netscape cookies.txt.</summary>
    public void SeedNetscape(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("#HttpOnly_")) line = line["#HttpOnly_".Length..];
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length < 7) continue;
            long.TryParse(f[4], out var expiry);
            Put(new Cookie(f[5], f[6], f[0].TrimStart('.').ToLowerInvariant(), !f[1].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                f[2].Length > 0 ? f[2] : "/", expiry > 0 ? expiry * 1000 : null));
        }
        Changed?.Invoke();
    }

    /// <summary>Cookies applicable to a URL, name → value.</summary>
    public Dictionary<string, string> For(string url)
    {
        var u = new Uri(url);
        var host = u.Host.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new Dictionary<string, string>();
        lock (_lock)
        {
            foreach (var c in _cookies)
            {
                if (c.Expires is { } exp && exp <= now) continue;
                var hostOk = c.HostOnly ? host == c.Domain : DomainMatches(host, c.Domain);
                if (hostOk && u.AbsolutePath.StartsWith(c.Path, StringComparison.Ordinal)) result[c.Name] = c.Value;
            }
        }
        return result;
    }

    public string Serialize()
    {
        lock (_lock) return JsonSerializer.Serialize(_cookies);
    }

    public void Load(string json)
    {
        var list = JsonSerializer.Deserialize<List<Cookie>>(json) ?? new();
        lock (_lock) _cookies = list;
    }

    /// <summary>Adds cookies obtained outside a response (sign-in in an embedded browser, pasted by the user).</summary>
    public void Add(IEnumerable<Cookie> cookies)
    {
        foreach (var cookie in cookies) Put(cookie with { Domain = cookie.Domain.TrimStart('.').ToLowerInvariant() });
        Changed?.Invoke();
    }

    /// <summary>Forgets every cookie (sign-out).</summary>
    public void Clear()
    {
        lock (_lock) _cookies = new();
        Changed?.Invoke();
    }

    /// <summary>Names of the cookies held for a domain (never values; for status checks and tests).</summary>
    public IReadOnlyList<string> NamesFor(string domain)
    {
        domain = domain.TrimStart('.').ToLowerInvariant();
        lock (_lock) return _cookies.Where(c => c.Domain == domain).Select(c => c.Name).ToList();
    }

    private void Put(Cookie cookie)
    {
        lock (_lock)
        {
            _cookies.RemoveAll(c => c.Name == cookie.Name && c.Domain == cookie.Domain && c.Path == cookie.Path);
            _cookies.Add(cookie);
        }
    }

    public static bool DomainMatches(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>Merges jar cookies with a script Cookie header; script values win on name clashes.</summary>
    public static string? MergeCookieHeader(Dictionary<string, string> jar, string? scriptHeader)
    {
        var merged = new Dictionary<string, string>(jar);
        var order = jar.Keys.ToList();
        if (!string.IsNullOrEmpty(scriptHeader))
        {
            foreach (var part in scriptHeader.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part[..eq].Trim();
                if (!merged.ContainsKey(name)) order.Add(name);
                merged[name] = part[(eq + 1)..].Trim();
            }
        }
        return merged.Count == 0 ? null : string.Join("; ", order.Select(k => $"{k}={merged[k]}"));
    }
}

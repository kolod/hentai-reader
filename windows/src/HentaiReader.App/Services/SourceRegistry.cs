using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HentaiReader.Host.Engine;
using HentaiReader.Host.Http;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;

namespace HentaiReader.App.Services;

/// <summary>A bundled source as the app shows it: open session, or the reason it cannot run.</summary>
public sealed class SourceEntry
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public SourceSession? Session { get; set; }
    public string? UnavailableReason { get; set; }
    public bool IsAvailable => Session is not null;

    /// <summary>The user's values of the declared settings (FR-026).</summary>
    public SourceSettingsStore? Settings { get; set; }

    /// <summary>The source's cookie jar (sign-in and sign-out, FR-028/FR-030).</summary>
    public CookieJar? Cookies { get; set; }

    /// <summary>The encrypted cookie file of this source.</summary>
    public string? CookieFile { get; set; }

    /// <summary>Sign-out: the source's cookies are forgotten and their file deleted (FR-030).</summary>
    public void SignOut()
    {
        Cookies?.Clear();
        if (CookieFile is not null && File.Exists(CookieFile)) File.Delete(CookieFile);
        LastAccountStatus = null;
    }

    /// <summary>Last getAccountStatus result (AccountStatus JSON), shown again where it matters (FR-027).</summary>
    public JsonObject? LastAccountStatus { get; set; }

    /// <summary>Calls a source function with the stored settings (manifest defaults for the rest).</summary>
    public Task<CallResult> CallAsync(string fn, IReadOnlyList<JsonNode?> args, CancellationToken token = default) =>
        Session!.CallAsync(fn, args, Settings?.Values, token);
}

/// <summary>
/// Opens one session per bundled source with persistent storage and a DPAPI-protected cookie jar
/// (data-model.md). Nothing here sends a request; sources only talk to sites when called.
/// </summary>
public sealed class SourceRegistry : IDisposable
{
    private readonly LiveTransport _transport = new();
    public List<SourceEntry> Entries { get; } = new();

    public async Task LoadAsync(string bundleDirectory, Preferences preferences)
    {
        foreach (var bundle in SourceCatalog.Load(bundleDirectory))
        {
            var entry = new SourceEntry { Id = bundle.Id, Name = bundle.Id };
            try
            {
                var dir = AppPaths.SourceData(bundle.Id);
                Directory.CreateDirectory(dir);
                entry.Cookies = LoadCookies(dir);
                entry.CookieFile = Path.Combine(dir, "cookies.bin");
                var options = new SessionOptions(_transport, EnforcePoliteness: true, new SourceStorage(Path.Combine(dir, "storage.json")), entry.Cookies);
                entry.Session = await SourceSession.OpenAsync(bundle.Script, Path.GetFileName(bundle.ScriptPath), options);
                entry.Settings = new SourceSettingsStore(preferences, bundle.Id, entry.Session.Manifest["settings"] as JsonArray);
                entry.Name = entry.Session.Manifest["name"]?.GetValue<string>() ?? bundle.Id;
            }
            catch (Exception e) when (e is LoadException or SettingsException or IOException)
            {
                entry.UnavailableReason = e.Message;
            }
            Entries.Add(entry);
        }
    }

    public SourceEntry? Find(string? id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>Cookies are stored only locally, encrypted for the current Windows user (Constitution VI).</summary>
    private static CookieJar LoadCookies(string dir)
    {
        var file = Path.Combine(dir, "cookies.bin");
        var jar = new CookieJar();
        try
        {
            if (File.Exists(file)) jar.Load(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser)));
        }
        catch (CryptographicException)
        {
            // Unreadable (other user or corrupted): start empty.
        }
        var gate = new object();
        jar.Changed += () =>
        {
            lock (gate)
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(jar.Serialize()), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(file, bytes);
            }
        };
        return jar;
    }

    public void Dispose()
    {
        foreach (var e in Entries) e.Session?.Dispose();
        _transport.Dispose();
    }
}

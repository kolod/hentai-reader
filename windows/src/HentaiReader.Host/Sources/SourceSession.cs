using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HentaiReader.Host.Engine;
using HentaiReader.Host.Html;
using HentaiReader.Host.Http;
using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Sources;

public enum SessionState { Idle, Busy, Limited, Broken }

public sealed record SessionOptions(
    ITransport Transport,
    bool EnforcePoliteness,
    SourceStorage Storage,
    CookieJar? Cookies = null,
    long? FrozenNow = null,
    EngineLimits? Limits = null,
    Action<string, string>? OnLog = null);

/// <summary>
/// One running source: engine, HTTP, HTML and storage, implementing the bridge. Tracks the state
/// machine of data-model.md: Limited after RateLimited (no requests until the reader retries),
/// Broken after a limit hit (fresh engine on the next call).
/// </summary>
public sealed partial class SourceSession : IHostBridge, IDisposable
{
    private readonly string _script;
    private readonly string _name;
    private readonly SessionOptions _options;
    private readonly HtmlHost _html = new();
    private SourceEngine? _engine;
    private Exception? _fatal;

    public HttpHost Http { get; }
    public JsonObject Manifest { get; private set; } = new();
    public IReadOnlyList<string> Functions { get; private set; } = [];
    public SessionState State { get; private set; } = SessionState.Idle;
    public double? RetryAfterSeconds { get; private set; }

    private SourceSession(string script, string name, SessionOptions options)
    {
        _script = script;
        _name = name;
        _options = options;
        Http = new HttpHost(options.Transport, options.EnforcePoliteness, options.Cookies) { OnFatal = e => _fatal ??= e };
    }

    [GeneratedRegex(@"^\d+\.\d+$")]
    private static partial Regex ContractPattern();

    /// <summary>Loads a source; throws <see cref="LoadException"/> if it cannot run on this host.</summary>
    public static async Task<SourceSession> OpenAsync(string script, string name, SessionOptions options)
    {
        var session = new SourceSession(script, name, options);
        await session.StartEngineAsync();
        return session;
    }

    private async Task StartEngineAsync()
    {
        _engine?.Dispose();
        _engine = await SourceEngine.CreateAsync(_script, _name, this, _options.FrozenNow, _options.Limits);
        var description = await _engine.DescribeAsync();
        var manifest = description.Manifest;
        var contract = manifest["contract"]?.GetValue<string>();
        if (contract is null || !ContractPattern().IsMatch(contract)) throw new LoadException($"manifest.contract must be \"MAJOR.MINOR\", got {contract}");
        if (!IsCompatible(HostInfo.ContractVersion, contract)) throw new LoadException($"source requires contract {contract}, host supports {HostInfo.ContractVersion[..HostInfo.ContractVersion.LastIndexOf('.')]}");
        foreach (var field in new[] { "id", "name", "version", "domains", "politeness", "login", "filters", "capabilities" })
        {
            if (manifest[field] is null) throw new LoadException($"invalid manifest: missing {field}");
        }
        var loginPage = ManifestRules.LoginPageViolations(manifest);
        if (loginPage.Count > 0) throw new LoadException("invalid manifest:\n  " + string.Join("\n  ", loginPage));
        Manifest = manifest;
        Functions = description.Functions;
        var politeness = manifest["politeness"]!;
        Http.SetManifest(new HttpManifest(
            manifest["domains"]!.AsArray().Select(d => d!.GetValue<string>()).ToList(),
            new Politeness(politeness["minIntervalMs"]!.GetValue<int>(), politeness["maxConcurrency"]!.GetValue<int>())));
    }

    public static bool IsCompatible(string hostVersion, string required)
    {
        var h = hostVersion.Split('.').Select(int.Parse).ToArray();
        var r = required.Split('.').Select(int.Parse).ToArray();
        return r[0] == h[0] && r[1] <= h[1];
    }

    /// <summary>Calls a source function following the calling convention.</summary>
    public async Task<CallResult> CallAsync(string fn, IReadOnlyList<JsonNode?> args, IReadOnlyDictionary<string, string>? settings = null, CancellationToken token = default)
    {
        if (State == SessionState.Limited)
        {
            return CallResult.Failure(new CallError("SourceError", SourceErrorKind.RateLimited, null,
                "the source is rate-limited; retry explicitly", RetryAfterSeconds));
        }
        if (State == SessionState.Broken || _engine?.Broken is not null) await StartEngineAsync();
        var callArgs = CallArgs.Build(Manifest, fn, args, CallArgs.ResolveSettings(Manifest, settings));
        State = SessionState.Busy;
        _fatal = null;
        var result = await _engine!.CallAsync(fn, callArgs.ToJsonString(), token);
        if (result.Error is { Type: "SourceError", Kind: SourceErrorKind.RateLimited } limited)
        {
            State = SessionState.Limited;
            RetryAfterSeconds = limited.RetryAfterSeconds;
        }
        else
        {
            State = _engine.Broken is null ? SessionState.Idle : SessionState.Broken;
        }
        return result;
    }

    /// <summary>
    /// Downloads the image of a contract ImageRequest (<c>{url, headers}</c>) with the source's
    /// cookies and limiter. HTTP 429/509 puts the session into <see cref="SessionState.Limited"/>
    /// like a RateLimited script result; while limited nothing is requested.
    /// </summary>
    public async Task<byte[]> FetchImageAsync(JsonNode request, CancellationToken token = default)
    {
        if (State == SessionState.Limited) throw new SourceLimitedException(RetryAfterSeconds);
        var url = request["url"]?.GetValue<string>() ?? throw new ArgumentException("ImageRequest without url", nameof(request));
        var headers = new Dictionary<string, string>();
        if (request["headers"] is JsonObject h)
        {
            foreach (var (k, v) in h)
            {
                if (v is JsonValue value && value.TryGetValue<string>(out var text)) headers[k] = text;
            }
        }
        try
        {
            return await Http.FetchImageAsync(url, headers, token);
        }
        catch (ImageRateLimitException e)
        {
            State = SessionState.Limited;
            RetryAfterSeconds = e.RetryAfterSeconds;
            throw new SourceLimitedException(e.RetryAfterSeconds);
        }
    }

    /// <summary>The reader explicitly retries after a rate limit (FR-013).</summary>
    public void ClearLimit()
    {
        if (State == SessionState.Limited) State = SessionState.Idle;
        RetryAfterSeconds = null;
    }

    // --- IHostBridge ---

    public Task<HttpBridgeResult> HttpAsync(string metaJson, byte[]? body) => Http.RequestAsync(metaJson, body);
    public string HtmlSelect(string markup, int elementId, string selector) => _html.Select(markup, elementId, selector);
    public string HtmlCall(string op, int elementId, string arg) => _html.Call(op, elementId, arg);
    public Task<string> StorageGetAsync(string key) => _options.Storage.GetAsync(key);
    public Task<string> StorageSetAsync(string key, string valueJson) => _options.Storage.SetAsync(key, valueJson);

    public async Task<string> SleepAsync(double ms)
    {
        if (double.IsNaN(ms) || ms < 0 || ms > 60_000) return Envelope.Error(SourceErrorKind.InvalidInput, "sleep: ms must be between 0 and 60000");
        if (_options.EnforcePoliteness) await Task.Delay(TimeSpan.FromMilliseconds(ms));
        return Envelope.Value(null);
    }

    public void Log(string level, string text) => _options.OnLog?.Invoke(level, text);
    public void Idle() => _html.ReleaseHandles();
    public Exception? Fatal => _fatal;

    public void Dispose() => _engine?.Dispose();
}

/// <summary>The source is rate-limited: nothing is requested until the user retries (FR-021).</summary>
public sealed class SourceLimitedException(double? retryAfterSeconds)
    : Exception("the source is rate-limited; retry explicitly")
{
    public double? RetryAfterSeconds { get; } = retryAfterSeconds;
}

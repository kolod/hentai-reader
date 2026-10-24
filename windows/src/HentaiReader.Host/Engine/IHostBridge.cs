namespace HentaiReader.Host.Engine;

/// <summary>Result of the bridge's http member: an envelope plus the body for "bytes" responses.</summary>
public sealed record HttpBridgeResult(string Env, byte[]? Bytes);

/// <summary>
/// Host side of the private bridge consumed by contract/host-glue.js
/// (specs/003-windows-host/contracts/host-bridge.md). Every member returns a JSON envelope.
/// </summary>
public interface IHostBridge
{
    Task<HttpBridgeResult> HttpAsync(string metaJson, byte[]? body);
    string HtmlSelect(string markup, int elementId, string selector);
    string HtmlCall(string op, int elementId, string arg);
    Task<string> StorageGetAsync(string key);
    Task<string> StorageSetAsync(string key, string valueJson);
    Task<string> SleepAsync(double ms);
    void Log(string level, string text);

    /// <summary>Called when no source call is in flight, so per-call handles can be dropped.</summary>
    void Idle();

    /// <summary>A failure that must abort the call whatever the script does (e.g. unmatched fixture).</summary>
    Exception? Fatal { get; }
}

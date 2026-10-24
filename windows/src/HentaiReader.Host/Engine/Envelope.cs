using System.Text.Json;

namespace HentaiReader.Host.Engine;

/// <summary>Error kinds of the contract (host-api.d.ts SourceErrorKind).</summary>
public static class SourceErrorKind
{
    public const string RateLimited = "RateLimited";
    public const string LoginRequired = "LoginRequired";
    public const string NotFound = "NotFound";
    public const string SiteChanged = "SiteChanged";
    public const string InvalidInput = "InvalidInput";
    public const string Network = "Network";
}

/// <summary>
/// JSON envelopes exchanged with contract/host-glue.js: <c>{"value": …}</c> or
/// <c>{"error": {"kind", "message", "options"}}</c> (contracts/host-bridge.md).
/// </summary>
public static class Envelope
{
    public static string Value(object? value) => JsonSerializer.Serialize(new Dictionary<string, object?> { ["value"] = value });

    public static string Error(string kind, string message, object? options = null)
    {
        var error = new Dictionary<string, object?> { ["kind"] = kind, ["message"] = message };
        if (options is not null) error["options"] = options;
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["error"] = error });
    }
}

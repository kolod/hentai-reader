using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HentaiReader.Host.Sources;

public sealed class SettingsException(string message) : Exception(message);

/// <summary>
/// Settings resolution and the contract 1.1 calling convention (port of resolveSettings,
/// takesSettings and callArgs from tools/runner/src/source.ts).
/// </summary>
public static partial class CallArgs
{
    private static readonly Dictionary<string, int> Arity = new()
    {
        ["search"] = 2, ["getGallery"] = 1, ["getPageUrl"] = 3, ["suggestTags"] = 1, ["getAccountStatus"] = 0,
    };

    [GeneratedRegex(@"^(\d+)\.(\d+)$")]
    private static partial Regex MajorMinor();

    /// <summary>Manifest defaults merged with overrides; unknown ids or values throw.</summary>
    public static JsonObject ResolveSettings(JsonObject manifest, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var defs = manifest["settings"] as JsonArray ?? new JsonArray();
        var result = new JsonObject();
        foreach (var d in defs) result[d!["id"]!.GetValue<string>()] = d["default"]!.GetValue<string>();
        foreach (var (id, value) in overrides ?? new Dictionary<string, string>())
        {
            var def = defs.FirstOrDefault(d => d!["id"]!.GetValue<string>() == id)
                ?? throw new SettingsException($"unknown setting '{id}'");
            if (!def["values"]!.AsArray().Any(v => v!.GetValue<string>() == value))
                throw new SettingsException($"setting {id}: '{value}' is not allowed");
            result[id] = value;
        }
        return result;
    }

    /// <summary>True when the source declares contract 1.1 or later.</summary>
    public static bool TakesSettings(JsonObject manifest)
    {
        var m = MajorMinor().Match(manifest["contract"]?.GetValue<string>() ?? "");
        return m.Success && (int.Parse(m.Groups[1].Value) > 1 || int.Parse(m.Groups[2].Value) >= 1);
    }

    /// <summary>Arguments as the calling convention requires for this source's contract version.</summary>
    public static JsonArray Build(JsonObject manifest, string fn, IReadOnlyList<JsonNode?> args, JsonObject settings)
    {
        if (fn == "getAccountStatus" && manifest["capabilities"]?["accountStatus"]?.GetValue<bool>() != true)
            throw new SettingsException("getAccountStatus is not declared in manifest.capabilities");
        if (!TakesSettings(manifest))
        {
            var given = args.Count > 2 && fn == "getPageUrl" && args[2] is null ? args.Take(2) : args;
            return new JsonArray(given.Select(a => a?.DeepClone()).ToArray());
        }
        var arity = Arity.TryGetValue(fn, out var n) ? n : args.Count;
        var result = new JsonArray();
        for (var i = 0; i < arity; i++) result.Add(i < args.Count ? args[i]?.DeepClone() : null);
        result.Add(settings.DeepClone());
        return result;
    }
}

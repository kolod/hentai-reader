using System.Text.Json.Nodes;
using HentaiReader.Host.Engine;
using HentaiReader.Host.Fixtures;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Tests.Parity;

/// <summary>
/// Replays one recorded test case (`tests/&lt;name&gt;.case.json`) against the Windows host, with the
/// runner's rules (port of tools/runner/src/cases.ts): fixture pool, frozen clock, storage seed,
/// settings, `@case[.path]` references resolved from snapshots. Snapshots are read-only here.
/// </summary>
internal static class CaseRunner
{
    public sealed record Outcome(CallResult Result, JsonObject Case, JsonNode? Expected);

    public static async Task<Outcome> RunAsync(string sourceDir, string caseName)
    {
        var name = Path.GetFileName(sourceDir);
        var tests = Path.Combine(sourceDir, "tests");
        var c = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(tests, caseName + ".case.json")))!.AsObject();
        var call = c["call"]!.AsObject();
        var args = Substitute(call["args"]!.DeepClone(), tests)!.AsArray().ToList();

        var fixtures = Path.Combine(sourceDir, "fixtures");
        if (c["fixtures"] is JsonValue pool) fixtures = Path.Combine(fixtures, pool.GetValue<string>());
        var transport = new OfflineTransport(fixtures);
        var storage = new SourceStorage();
        if (c["storage"] is JsonObject seed) storage.Seed(seed.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()));
        var options = new SessionOptions(transport, EnforcePoliteness: false, storage, FrozenNow: transport.RecordedAt);

        using var session = await SourceSession.OpenAsync(await File.ReadAllTextAsync(Path.Combine(sourceDir, name + ".js")), name + ".js", options);
        var settings = c["settings"] is JsonObject s ? s.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) : null;
        var result = await session.CallAsync(call["fn"]!.GetValue<string>(), args, settings);
        var expectedPath = Path.Combine(tests, caseName + ".expected.json");
        var expected = File.Exists(expectedPath) ? JsonNode.Parse(await File.ReadAllTextAsync(expectedPath)) : null;
        return new Outcome(result, c, expected);
    }

    /// <summary>Replaces "@case" / "@case.path" strings at any depth with snapshot values.</summary>
    private static JsonNode? Substitute(JsonNode? node, string testsDir)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('@'):
            {
                var parts = s[1..].Split('.');
                var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(testsDir, parts[0] + ".expected.json")));
                foreach (var key in parts.Skip(1))
                {
                    snapshot = snapshot switch
                    {
                        JsonObject o => o[key],
                        JsonArray a when int.TryParse(key, out var i) => a[i],
                        _ => null,
                    };
                }
                return snapshot?.DeepClone();
            }
            case JsonArray a:
                return new JsonArray(a.Select(x => Substitute(x, testsDir)).ToArray());
            case JsonObject o:
            {
                var result = new JsonObject();
                foreach (var (k, x) in o) result[k] = Substitute(x, testsDir);
                return result;
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Paths where two JSON values differ (for readable failures).</summary>
    public static IEnumerable<string> Diff(JsonNode? expected, JsonNode? actual, string path = "")
    {
        if (JsonNode.DeepEquals(expected, actual)) yield break;
        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            foreach (var key in eo.Select(kv => kv.Key).Union(ao.Select(kv => kv.Key)).Order(StringComparer.Ordinal))
            {
                var p = path.Length == 0 ? key : $"{path}.{key}";
                if (!ao.ContainsKey(key)) yield return $"- {p}";
                else if (!eo.ContainsKey(key)) yield return $"+ {p}: {Show(ao[key])}";
                else foreach (var d in Diff(eo[key], ao[key], p)) yield return d;
            }
            yield break;
        }
        if (expected is JsonArray ea && actual is JsonArray aa)
        {
            for (var i = 0; i < Math.Max(ea.Count, aa.Count); i++)
            {
                var p = $"{path}[{i}]";
                if (i >= aa.Count) yield return $"- {p}";
                else if (i >= ea.Count) yield return $"+ {p}: {Show(aa[i])}";
                else foreach (var d in Diff(ea[i], aa[i], p)) yield return d;
            }
            yield break;
        }
        yield return $"~ {(path.Length == 0 ? "(root)" : path)}: {Show(expected)} → {Show(actual)}";
    }

    private static string Show(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "null";
        return s.Length > 80 ? s[..77] + "…" : s;
    }
}

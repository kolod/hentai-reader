using System.Text.Json.Nodes;
using HentaiReader.Host.Fixtures;
using HentaiReader.Host.Http;
using HentaiReader.Host.Sources;
using HentaiReader.Host.Storage;

namespace HentaiReader.Host.Tests.Parity;

/// <summary>
/// The portable host contract suite (contract/tests/*) run against the Windows host, exactly as
/// `source-runner contract-test` runs it (contracts/parity-suite.md §1).
/// </summary>
public class ContractSuite
{
    private const int GapToleranceMs = 2;

    public static TheoryData<string> Tests() =>
        new(Directory.GetDirectories(RepoPaths.ContractTests)
            .Where(d => File.Exists(Path.Combine(d, "test.js")))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)!);

    [Theory(DisplayName = "Contract")]
    [MemberData(nameof(Tests))]
    public async Task Contract(string name)
    {
        var dir = Path.Combine(RepoPaths.ContractTests, name);
        var expectPath = Path.Combine(dir, "expect.json");
        var expect = File.Exists(expectPath) ? JsonNode.Parse(await File.ReadAllTextAsync(expectPath, TestContext.Current.CancellationToken))!.AsObject() : new JsonObject();
        var problems = new List<string>();

        async Task<List<RequestLogEntry>> Execute(string script)
        {
            var cookies = new CookieJar();
            if (expect["cookies"]?["seed"] is JsonObject seed)
            {
                foreach (var (domain, value) in seed) cookies.Seed(domain, value!.GetValue<string>());
            }
            // Storage is per script: the isolation test runs two different sources.
            var options = new SessionOptions(new OfflineTransport(Path.Combine(dir, "fixtures")), EnforcePoliteness: true, new SourceStorage(), cookies);
            using var session = await SourceSession.OpenAsync(await File.ReadAllTextAsync(Path.Combine(dir, script), TestContext.Current.CancellationToken), script, options);
            var settings = expect["settings"] is JsonObject s ? s.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) : null;
            var res = await session.CallAsync("run", [], settings, TestContext.Current.CancellationToken);
            if (!res.Ok)
            {
                var e = res.Error!;
                problems.Add($"{script}: {(e.Type == "SourceError" ? $"SourceError({e.Kind})" : e.Name)}: {e.Message}");
            }
            lock (session.Http.Log) return session.Http.Log.ToList();
        }

        if (expect["other"] is JsonValue other) await Execute(other.GetValue<string>());
        var log = await Execute("test.js");
        problems.AddRange(AssertLog(log, expect));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static IEnumerable<string> AssertLog(List<RequestLogEntry> log, JsonObject expect)
    {
        var sent = log.Where(r => r.Refused is null).ToList();
        if (expect["minGapMs"] is JsonValue gapNode)
        {
            var minGap = gapNode.GetValue<int>();
            var starts = sent.Select(r => r.StartedAt).Order().ToList();
            if (starts.Count < 2) yield return "minGapMs: fewer than two requests were sent";
            for (var i = 1; i < starts.Count; i++)
            {
                var gap = starts[i] - starts[i - 1];
                if (gap < minGap - GapToleranceMs) yield return $"request start gap {gap} ms < minGapMs {minGap}";
            }
        }
        if (expect["maxInFlight"] is JsonValue maxNode)
        {
            var max = 0;
            foreach (var r in sent)
            {
                var overlapping = sent.Count(o => !ReferenceEquals(o, r) && o.StartedAt <= r.StartedAt && (o.EndedAt ?? long.MaxValue) > r.StartedAt);
                max = Math.Max(max, overlapping + 1);
            }
            var expected = maxNode.GetValue<int>();
            if (max > expected) yield return $"{max} requests in flight > maxInFlight {expected}";
            if (max < expected) yield return $"only {max} requests in flight; the test does not exercise maxInFlight {expected}";
        }
        foreach (var url in expect["refusedUrls"]?.AsArray().Select(u => u!.GetValue<string>()) ?? [])
        {
            if (!log.Any(r => r.Refused is not null && r.Url == url)) yield return $"expected the host to refuse {url}";
        }
        string HostOf(RequestLogEntry r) => new Uri(r.Url).Host;
        foreach (var host in expect["cookies"]?["sentTo"]?.AsArray().Select(h => h!.GetValue<string>()) ?? [])
        {
            if (!sent.Any(r => HostOf(r) == host && r.Headers.ContainsKey("cookie"))) yield return $"expected cookies to be sent to {host}";
        }
        foreach (var host in expect["cookies"]?["notSentTo"]?.AsArray().Select(h => h!.GetValue<string>()) ?? [])
        {
            if (sent.Any(r => HostOf(r) == host && r.Headers.ContainsKey("cookie"))) yield return $"cookies must not be sent to {host}";
        }
    }
}

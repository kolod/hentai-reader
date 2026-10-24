namespace HentaiReader.Host.Tests.Parity;

/// <summary>
/// Every recorded case of every source and conformance sample, replayed on the Windows host and
/// compared with the runner's snapshot (contracts/parity-suite.md §2).
/// </summary>
public class SourceCases
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var root in new[] { RepoPaths.Sources, RepoPaths.Conformance })
        {
            foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                var tests = Path.Combine(dir, "tests");
                if (!File.Exists(Path.Combine(dir, Path.GetFileName(dir) + ".js")) || !Directory.Exists(tests)) continue;
                // Broken samples are covered by ConformanceTests (their cases expect violations).
                if (Path.GetFileName(dir).StartsWith("broken-", StringComparison.Ordinal)) continue;
                foreach (var file in Directory.GetFiles(tests, "*.case.json").Order(StringComparer.Ordinal))
                {
                    data.Add(Path.GetRelativePath(RepoPaths.Root, dir).Replace('\\', '/'), Path.GetFileName(file)[..^".case.json".Length]);
                }
            }
        }
        return data;
    }

    [Theory(DisplayName = "Case")]
    [MemberData(nameof(Cases))]
    public async Task Case(string source, string name)
    {
        var outcome = await CaseRunner.RunAsync(Path.Combine(RepoPaths.Root, source), name);
        var result = outcome.Result;
        if (outcome.Case["expectError"]?["kind"]?.GetValue<string>() is { } kind)
        {
            Assert.False(result.Ok, $"expected SourceError({kind}), got a result");
            Assert.True(result.Error!.Type == "SourceError" && result.Error.Kind == kind,
                $"expected SourceError({kind}), got {result.Error.Type}({result.Error.Kind ?? result.Error.Name}): {result.Error.Message}");
            return;
        }
        Assert.True(result.Ok, $"unexpected {result.Error?.Type}({result.Error?.Kind ?? result.Error?.Name}): {result.Error?.Message}");
        Assert.NotNull(outcome.Expected);
        var diff = CaseRunner.Diff(outcome.Expected, result.Value).Take(30).ToList();
        Assert.True(diff.Count == 0, "result differs from the runner's snapshot:\n" + string.Join("\n", diff));
    }
}

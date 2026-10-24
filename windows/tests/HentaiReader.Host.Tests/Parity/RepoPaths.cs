using System.Reflection;

namespace HentaiReader.Host.Tests.Parity;

/// <summary>Repository locations used by the parity suite (contract tests, sources, fixtures).</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Find();

    public static string ContractTests => Path.Combine(Root, "contract", "tests");
    public static string Sources => Path.Combine(Root, "sources");
    public static string Conformance => Path.Combine(Root, "contract", "conformance");

    private static string Find()
    {
        var fromBuild = typeof(RepoPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
        if (fromBuild is not null && File.Exists(Path.Combine(fromBuild, "contract", "host-api.d.ts"))) return fromBuild;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "contract", "host-api.d.ts"))) return dir.FullName;
        }
        throw new InvalidOperationException("repository root not found");
    }
}

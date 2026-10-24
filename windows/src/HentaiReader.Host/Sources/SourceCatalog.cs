namespace HentaiReader.Host.Sources;

/// <summary>A bundled source script (Constitution III: the app's built-in copy).</summary>
public sealed record SourceBundle(string Id, string ScriptPath)
{
    public string Script => File.ReadAllText(ScriptPath);
}

/// <summary>Finds bundled scripts laid out as <c>&lt;base&gt;/&lt;id&gt;/&lt;id&gt;.js</c>.</summary>
public static class SourceCatalog
{
    public static IReadOnlyList<SourceBundle> Load(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory)) return [];
        return Directory.GetDirectories(baseDirectory)
            .Select(dir => new SourceBundle(Path.GetFileName(dir), Path.Combine(dir, Path.GetFileName(dir) + ".js")))
            .Where(b => File.Exists(b.ScriptPath))
            .OrderBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
    }
}

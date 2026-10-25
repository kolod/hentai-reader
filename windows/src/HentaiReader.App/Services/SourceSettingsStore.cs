using System.Text.Json.Nodes;

namespace HentaiReader.App.Services;

/// <summary>
/// The user's values for the settings a source declares (manifest.settings, FR-026), kept in
/// preferences. Values the manifest does not declare (old ids, removed values) are ignored, so the
/// defaults apply and every call receives only valid settings.
/// </summary>
public sealed class SourceSettingsStore(Preferences preferences, string sourceId, JsonArray? declared)
{
    /// <summary>Current values, only for declared ids with a declared value; empty = all defaults.</summary>
    public IReadOnlyDictionary<string, string> Values
    {
        get
        {
            var result = new Dictionary<string, string>();
            if (!preferences.SourceSettings.TryGetValue(sourceId, out var stored)) return result;
            foreach (var (id, value) in stored)
            {
                if (IsDeclared(id, value)) result[id] = value;
            }
            return result;
        }
    }

    /// <summary>The declared settings: id, allowed values and default.</summary>
    public IEnumerable<(string Id, IReadOnlyList<string> Values, string Default)> Declared =>
        (declared ?? []).OfType<JsonObject>().Select(s => (
            s["id"]!.GetValue<string>(),
            (IReadOnlyList<string>)s["values"]!.AsArray().Select(v => v!.GetValue<string>()).ToList(),
            s["default"]!.GetValue<string>()));

    public string ValueOf(string id) =>
        Values.TryGetValue(id, out var value) ? value : Declared.FirstOrDefault(d => d.Id == id).Default;

    public void Set(string id, string value)
    {
        if (!IsDeclared(id, value)) throw new ArgumentException($"{sourceId} does not declare {id}={value}");
        if (!preferences.SourceSettings.TryGetValue(sourceId, out var stored))
            preferences.SourceSettings[sourceId] = stored = new Dictionary<string, string>();
        stored[id] = value;
        preferences.Save();
    }

    private bool IsDeclared(string id, string value) => Declared.Any(d => d.Id == id && d.Values.Contains(value));
}

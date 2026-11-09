using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DcxManager.Services;

/// <summary>
/// Settings for module add-ons (ADR-120). A module keeps its own settings class; <see cref="Attach{T}"/> loads it from the
/// encrypted settings file (<see cref="AppSettings.ModuleData"/>), puts Group Policy's values over it, and writes it back
/// with every settings save. Nothing about a module's settings is compiled into the app, so a module add-on can add or
/// change settings without an app release.
/// <para>Migration: settings that once belonged to the app and moved into a module are still in the file
/// (<see cref="AppSettings.Retired"/>). The first time the module attaches, the values whose names match its properties
/// move into its settings.</para>
/// </summary>
public static class ModuleSettings
{
    private static readonly Dictionary<string, Func<JsonObject>> Writers = new(StringComparer.Ordinal);
    private static bool _hooked;

    /// <summary>A module id: lower-case letters, digits, dots and hyphens.</summary>
    public static bool ValidId(string id) => id.Length is > 0 and <= 64 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-');

    /// <summary>Loads the module's settings (or a new object) and keeps it saved with the app's settings.</summary>
    public static T Attach<T>(string module) where T : class, new()
    {
        if (!ValidId(module)) throw new ArgumentException("not a module id: " + module);
        var s = AppSettings.Current;
        var node = s.ModuleData.GetValueOrDefault(module);
        bool migrated = false;
        if (node is null && s.Retired is { Count: > 0 } retired)
        {
            var names = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var take = retired.Keys.Where(names.Contains).ToList();
            if (take.Count > 0)
            {
                node = new JsonObject();
                foreach (var k in take) { node[k] = JsonNode.Parse(retired[k].GetRawText()); retired.Remove(k); }
                migrated = true;
            }
        }
        T value;
        try { value = node?.Deserialize<T>(SecureStore.Json) ?? new T(); }
        catch (JsonException ex) { Log.Write("module settings " + module, ex); value = new T(); }
        lock (Writers)
        {
            Writers[module] = () => JsonSerializer.SerializeToNode(value, SecureStore.Json) as JsonObject ?? new JsonObject();
            if (!_hooked) { _hooked = true; AppSettings.Saving += Flush; }
        }
        if (migrated)
        {
            AuditLog.Write("app", "settings", "module settings moved out of the app's settings", module);
            AppSettings.Save();
        }
        return value;
    }

    /// <summary>Writes every attached module's settings into the settings file's data (before each save), with policy over them.</summary>
    private static void Flush()
    {
        List<(string, Func<JsonObject>)> all;
        lock (Writers) all = Writers.Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var (module, write) in all)
        {
            PolicySettings.ApplyToModule(module);
            AppSettings.Current.ModuleData[module] = write();
        }
    }

    /// <summary>Self-test: stop saving a test module's settings.</summary>
    internal static void DetachForTest(string module) { lock (Writers) Writers.Remove(module); }

    /// <summary>The modules attached in this run (self-test).</summary>
    public static IReadOnlyList<string> Attached { get { lock (Writers) return Writers.Keys.ToList(); } }
}

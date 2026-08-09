using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortProSage.Admin.Services;

/// <summary>
/// Thin wrapper over System.Text.Json.Nodes.JsonNode for editing one settings
/// file by dotted path (e.g. "PortProSage.Sync.PollingIntervalMinutes").
/// Deliberately NOT a strongly-typed POCO round-trip - a field this app
/// doesn't know about (e.g. "_comment", "_chargeAccountMapComment", or any
/// future setting) is read from the tree and written back to the SAME tree
/// untouched, so Save() can never silently drop something a human wrote into
/// the file by hand.
/// </summary>
public class JsonFileEditor
{
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    public string FilePath { get; }
    private JsonObject _root;

    public JsonFileEditor(string filePath)
    {
        FilePath = filePath;
        _root = File.Exists(filePath)
            ? (JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? new JsonObject())
            : new JsonObject();
    }

    public bool FileExists => File.Exists(FilePath);

    private static string[] SplitPath(string path) => path.Split('.', StringSplitOptions.RemoveEmptyEntries);

    private JsonNode? Navigate(string path, bool createMissing)
    {
        var segments = SplitPath(path);
        JsonObject current = _root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is JsonObject child)
            {
                current = child;
            }
            else if (createMissing)
            {
                var newChild = new JsonObject();
                current[segments[i]] = newChild;
                current = newChild;
            }
            else
            {
                return null;
            }
        }
        return current[segments[^1]];
    }

    public string GetString(string path, string defaultValue = "") =>
        Navigate(path, false)?.GetValue<string>() ?? defaultValue;

    public bool GetBool(string path, bool defaultValue = false) =>
        Navigate(path, false) is JsonValue v && v.TryGetValue(out bool b) ? b : defaultValue;

    public int GetInt(string path, int defaultValue = 0) =>
        Navigate(path, false) is JsonValue v && v.TryGetValue(out int i) ? i : defaultValue;

    public JsonArray GetArray(string path)
    {
        return Navigate(path, false) as JsonArray ?? new JsonArray();
    }

    public List<string> GetStringArray(string path) =>
        GetArray(path).Select(n => n?.GetValue<string>() ?? "").ToList();

    public void SetStringArray(string path, IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            array.Add(JsonValue.Create(v.Trim()));
        }
        SetLeaf(path, array);
    }

    /// <summary>Reads a flat string-keyed, string-valued JSON object (e.g. TaxCodesByAbbreviation).</summary>
    public List<KeyValuePair<string, string>> GetStringDictionary(string path)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (Navigate(path, false) is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                result.Add(new KeyValuePair<string, string>(key, value?.GetValue<string>() ?? ""));
            }
        }
        return result;
    }

    public void SetStringDictionary(string path, IEnumerable<KeyValuePair<string, string>> values)
    {
        var obj = new JsonObject();
        foreach (var kvp in values)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key)) continue;
            obj[kvp.Key] = JsonValue.Create(kvp.Value);
        }
        SetLeaf(path, obj);
    }

    public void SetString(string path, string value) => SetLeaf(path, JsonValue.Create(value));

    /// <summary>Writes an actual JSON null (not an empty string) when value is
    /// blank - for a nullable setting like SyncSettings.CutoffInvoiceDate, where
    /// "" would fail to bind as DateTimeOffset? on the Service side, but a real
    /// JSON null correctly binds to null (cutoff disabled).</summary>
    public void SetOptionalString(string path, string? value) =>
        SetLeaf(path, string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value));
    public void SetBool(string path, bool value) => SetLeaf(path, JsonValue.Create(value));
    public void SetInt(string path, int value) => SetLeaf(path, JsonValue.Create(value));
    public void SetArray(string path, JsonArray value) => SetLeaf(path, value);

    private void SetLeaf(string path, JsonNode? value)
    {
        var segments = SplitPath(path);
        JsonObject current = _root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is JsonObject child)
            {
                current = child;
            }
            else
            {
                var newChild = new JsonObject();
                current[segments[i]] = newChild;
                current = newChild;
            }
        }
        current[segments[^1]] = value;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, _root.ToJsonString(SaveOptions));
    }
}

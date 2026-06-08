using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Deterministic SHA-256 fingerprint over the canonical agent + tool declaration shape.
///
/// The hub uses this to short-circuit re-registration: if a connector reconnects
/// with the same fingerprint, the hub skips the round-trip to Laravel and replies
/// "accepted" immediately. An EMPTY fingerprint trivially matches the hub's initial
/// empty store value, so we must NEVER return "". See Python v0.2.1 fix.
///
/// Port of vested_connect/runtime/fingerprint.py and node/runtime/fingerprint.ts.
/// The JSON shape matches Python's json.dumps(sort_keys=True, separators=(",",":")).
/// </summary>
internal static class Fingerprint
{
    /// <summary>
    /// Returns a 64-char lowercase SHA-256 hex string over the canonical declaration shape.
    /// Never returns an empty string.
    /// </summary>
    public static string Compute(
        IReadOnlyList<AgentDeclaration> agents,
        IReadOnlyDictionary<string, ToolDeclaration> tools)
    {
        var canonical = BuildCanonical(agents, tools);
        var json = CanonicalJsonStringify(canonical);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, object?> BuildCanonical(
        IReadOnlyList<AgentDeclaration> agents,
        IReadOnlyDictionary<string, ToolDeclaration> tools)
    {
        var sortedAgents = agents
            .OrderBy(a => a.Key)
            .Select(a => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = a.Description,
                ["instructions"] = a.Instructions
                    .OrderBy(i => i.Position)
                    .Select(i => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["body"]     = i.Body,
                        ["format"]   = i.Format,
                        ["position"] = (object?)i.Position,
                        ["type"]     = i.Type,
                    })
                    .ToList(),
                ["key"]          = a.Key,
                ["model"]        = a.Model,
                ["model_config"] = (object?)null,
                ["name"]         = !string.IsNullOrEmpty(a.Name) ? a.Name : a.Key,
                ["status"]       = a.Status,
            })
            .ToList();

        var sortedTools = tools
            .OrderBy(kvp => kvp.Key)
            .Select(kvp =>
            {
                var t = kvp.Value;
                // Parse the schema JSON to include as a nested object, mirroring Python/Node.
                object? inputSchema = ParseSchemaOrNull(t.InputSchemaJson);
                object? outputSchema = t.OutputSchemaJson != null
                    ? ParseSchemaOrNull(t.OutputSchemaJson)
                    : null;

                return (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["default_deadline_ms"] = (object?)t.DefaultDeadlineMs,
                    ["description"]         = t.Description,
                    ["input_schema"]        = inputSchema,
                    ["key"]                 = t.Key,
                    ["max_result_bytes"]    = (object?)t.MaxResultBytes,
                    ["name"]                = t.Name,
                    ["output_schema"]       = outputSchema,
                    ["sensitivity"]         = t.Sensitivity,
                };
            })
            .ToList();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["agents"] = sortedAgents,
            ["tools"]  = sortedTools,
        };
    }

    private static object? ParseSchemaOrNull(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonElementToObject(doc.RootElement);
        }
        catch
        {
            return json;
        }
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Aggregate(
                new SortedDictionary<string, object?>(StringComparer.Ordinal),
                (d, p) => { d[p.Name] = JsonElementToObject(p.Value); return d; }),
        JsonValueKind.Array => el.EnumerateArray()
            .Select(JsonElementToObject)
            .ToList(),
        JsonValueKind.String  => (object?)el.GetString(),
        JsonValueKind.Number  => el.TryGetInt64(out var l) ? (object?)l : el.GetDouble(),
        JsonValueKind.True    => true,
        JsonValueKind.False   => false,
        JsonValueKind.Null    => null,
        _                     => el.GetRawText(),
    };

    /// <summary>
    /// Recursively serialises <paramref name="value"/> with sorted object keys
    /// and no extra whitespace — matches Python's json.dumps(sort_keys=True, separators=(",",":")).
    /// </summary>
    private static string CanonicalJsonStringify(object? value)
    {
        if (value is null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is int i) return i.ToString();
        if (value is long l) return l.ToString();
        if (value is double d) return d.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        if (value is string s) return JsonSerializer.Serialize(s);

        if (value is List<object?> list)
            return "[" + string.Join(",", list.Select(CanonicalJsonStringify)) + "]";

        if (value is SortedDictionary<string, object?> dict)
        {
            var parts = dict.Select(kvp =>
                JsonSerializer.Serialize(kvp.Key) + ":" + CanonicalJsonStringify(kvp.Value));
            return "{" + string.Join(",", parts) + "}";
        }

        if (value is Dictionary<string, object?> unsorted)
        {
            var parts = unsorted.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp =>
                    JsonSerializer.Serialize(kvp.Key) + ":" + CanonicalJsonStringify(kvp.Value));
            return "{" + string.Join(",", parts) + "}";
        }

        // Fallback: use JSON serializer (handles primitives we didn't match above).
        return JsonSerializer.Serialize(value);
    }
}

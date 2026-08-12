using System.Text.Json;

namespace VestedAI.ConnectorSdk.Schema;

public record SchemaDescribeArgs(string Scope, string Part);

/// <summary>
/// Exposes a provider's canonical model as an ordinary rowset tool.
/// </summary>
/// <remarks>
/// Deliberately a tool rather than a new hub op. The catalog is 26,191 tables
/// and hundreds of thousands of columns for a single company, which will never
/// fit one response — and the rowset path already solves that with the dataset
/// sink and the SDK's 16KB chunking. Inventing a second streaming mechanism
/// would be a parallel copy of one that works.
/// </remarks>
public sealed class SchemaDescribeTool
{
    private readonly IRelationalSchemaProvider _provider;

    public SchemaDescribeTool(IRelationalSchemaProvider provider) => _provider = provider;

    public async Task<IReadOnlyList<Dictionary<string, object?>>> DescribeAsync(
        SchemaDescribeArgs args,
        CancellationToken ct)
    {
        var schema = await _provider.DescribeAsync(args.Scope, ct).ConfigureAwait(false);

        return args.Part switch
        {
            "entities"  => schema.Entities.Select(ToRow).ToList(),
            "relations" => schema.Relations.Select(ToRow).ToList(),
            // An unknown part must not return an empty rowset: that ingests as
            // "this scope genuinely has none", producing a snapshot with no
            // joins that looks complete.
            _ => throw new ArgumentException(
                $"unknown part '{args.Part}'; expected 'entities' or 'relations'", nameof(args)),
        };
    }

    private static Dictionary<string, object?> ToRow<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, CanonicalJson.Options);

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
               ?? new Dictionary<string, object?>();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace VestedAI.ConnectorSdk.Schema;

/// <summary>
/// Serialization settings for the canonical schema wire format.
/// </summary>
/// <remarks>
/// snake_case, deliberately. The core reads these rows with
/// CanonicalEntity::fromArray(), which keys on snake_case, while this SDK
/// serialises PascalCase everywhere else. That exact mismatch is why the
/// production run_sql argument is "Sql" and why reading "sql" returned null
/// for 3,634 calls before anyone noticed.
/// </remarks>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

public record CanonicalVariant(string PhysicalName, string Role, int Ordinal);

public record CanonicalColumn(
    string Name,
    string Type,
    bool Nullable,
    int Position,
    bool IsPk,
    string? Caption,
    string VariantPhysicalName);

/// <summary>
/// One logical entity and the physical tables that make it up.
/// </summary>
/// <remarks>
/// The variant set is the point. `Item` is 8 physical tables for company ASG
/// and `LSC Retail Setup` is 14 — a base table plus its table-extensions,
/// joined on the primary key. INFORMATION_SCHEMA has no column that says so.
/// </remarks>
public record CanonicalEntity(
    string LogicalName,
    string ScopeKey,
    string Kind,
    string? Comment,
    IReadOnlyList<string> JoinKey,
    IReadOnlyList<CanonicalVariant> Variants,
    IReadOnlyList<CanonicalColumn> Columns);

public record CanonicalRelation(
    string FromEntity,
    IReadOnlyList<string> FromColumns,
    string ToEntity,
    IReadOnlyList<string> ToColumns,
    string Kind);

public record CanonicalSchema(
    IReadOnlyList<CanonicalEntity> Entities,
    IReadOnlyList<CanonicalRelation> Relations);

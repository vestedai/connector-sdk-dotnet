using System.Security.Cryptography;
using System.Text;

namespace VestedAI.ConnectorSdk.Schema;

/// <summary>
/// Builds the canonical model for Microsoft SQL Server hosting Business
/// Central / LS Central.
/// </summary>
public sealed class SqlServerProvider : IRelationalSchemaProvider
{
    private readonly ICatalogReader _reader;

    public SqlServerProvider(ICatalogReader reader) => _reader = reader;

    public async Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
    {
        var tables = await _reader.TablesAsync(ct).ConfigureAwait(false);

        return tables
            .Select(t => BcPhysicalName.TryParse(t.Name, out var p) ? p.Company : null)
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string> CatalogFingerprintAsync(CancellationToken ct)
    {
        var tables = await _reader.TablesAsync(ct).ConfigureAwait(false);

        var joined = string.Join('\n', tables
            .Select(t => $"{t.Schema}.{t.Name}")
            .OrderBy(s => s, StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    public async Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
    {
        var tables = await _reader.TablesAsync(ct).ConfigureAwait(false);
        var columns = await _reader.ColumnsAsync(ct).ConfigureAwait(false);
        var links = await _reader.ExtensionLinksAsync(ct).ConfigureAwait(false);

        var columnsByTable = columns
            .GroupBy(c => c.TableName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // extension physical name → its base physical name, from BC's own
        // metadata when readable.
        var baseOf = links.ToDictionary(l => l.ExtensionTable, l => l.BaseTable, StringComparer.Ordinal);

        // Group by logical name within the requested company. Tables with no
        // company prefix ($ndo$ internals, "Access Control" and the other 105
        // system tables) parse as failures and drop out here.
        var groups = tables
            .Select(t => BcPhysicalName.TryParse(t.Name, out var p)
                ? new { Table = t, Parsed = (BcPhysicalName?)p }
                : new { Table = t, Parsed = (BcPhysicalName?)null })
            .Where(x => x.Parsed is not null && string.Equals(x.Parsed.Value.Company, scopeKey, StringComparison.Ordinal))
            .GroupBy(x => x.Parsed!.Value.LogicalName, StringComparer.Ordinal);

        var entities = new List<CanonicalEntity>();
        var relations = new List<CanonicalRelation>();

        foreach (var group in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var physicalNames = group
                .Select(x => x.Table.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            var basePhysical = PickBase(physicalNames, baseOf, columnsByTable);

            var variants = new List<CanonicalVariant>();
            var ordinal = 0;
            variants.Add(new CanonicalVariant(basePhysical, "base", ordinal++));
            foreach (var name in physicalNames.Where(n => !string.Equals(n, basePhysical, StringComparison.Ordinal)))
            {
                variants.Add(new CanonicalVariant(name, "extension", ordinal++));
            }

            var joinKey = columnsByTable.TryGetValue(basePhysical, out var baseCols)
                ? baseCols.Where(c => c.IsPrimaryKey)
                    .OrderBy(c => c.OrdinalPosition)
                    .Select(c => c.ColumnName)
                    .ToList()
                : new List<string>();

            var entityColumns = new List<CanonicalColumn>();
            foreach (var variant in variants)
            {
                if (!columnsByTable.TryGetValue(variant.PhysicalName, out var cols))
                {
                    continue;
                }

                foreach (var c in cols.OrderBy(c => c.OrdinalPosition))
                {
                    entityColumns.Add(new CanonicalColumn(
                        Name: c.ColumnName,
                        Type: c.DataType,
                        Nullable: c.IsNullable,
                        Position: c.OrdinalPosition,
                        IsPk: c.IsPrimaryKey,
                        // CatalogColumn carries no caption field. Duplicating
                        // the column name here would fabricate a value that
                        // looks like curated business vocabulary while
                        // carrying zero information beyond the column name
                        // already stored beside it — real BC captions live in
                        // metadata INFORMATION_SCHEMA does not expose.
                        Caption: null,
                        VariantPhysicalName: variant.PhysicalName));
                }
            }

            entities.Add(new CanonicalEntity(
                LogicalName: group.Key,
                ScopeKey: scopeKey,
                Kind: "table",
                Comment: null,
                JoinKey: joinKey,
                Variants: variants,
                Columns: entityColumns));

            // Exactly one variant_join per entity that has more than one
            // variant and a non-empty join key — not one per extension. Both
            // endpoints are this entity and both column lists are the join
            // key: the fact worth recording is "this entity requires an
            // internal join on these columns", stated once, not repeated
            // once per extension table (which would be N byte-identical rows
            // carrying no distinguishing information). This is the knowledge
            // INFORMATION_SCHEMA does not carry and the reason the agent
            // queried an entity's variants as unrelated tables.
            if (variants.Count > 1 && joinKey.Count > 0)
            {
                relations.Add(new CanonicalRelation(
                    FromEntity: group.Key,
                    FromColumns: joinKey,
                    ToEntity: group.Key,
                    ToColumns: joinKey,
                    Kind: "variant_join"));
            }
        }

        return new CanonicalSchema(entities, relations);
    }

    /// <summary>
    /// Picks the base table of a variant set.
    /// </summary>
    /// <remarks>
    /// BC's own <c>$ndo$navapptableextension</c> is authoritative and is used
    /// when readable. When it is not, the fallback is "most columns wins",
    /// because an extension table carries the primary key plus only the fields
    /// it adds. The fallback is deliberate and documented rather than silent:
    /// getting it wrong mislabels roles but never loses a variant, so the join
    /// still works.
    /// </remarks>
    private static string PickBase(
        List<string> physicalNames,
        Dictionary<string, string> baseOf,
        Dictionary<string, List<CatalogColumn>> columnsByTable)
    {
        var declaredBase = physicalNames.FirstOrDefault(n =>
            !baseOf.ContainsKey(n) && physicalNames.Any(o => baseOf.TryGetValue(o, out var b)
                && string.Equals(b, n, StringComparison.Ordinal)));

        if (declaredBase is not null)
        {
            return declaredBase;
        }

        return physicalNames
            .OrderByDescending(n => columnsByTable.TryGetValue(n, out var c) ? c.Count : 0)
            .ThenBy(n => n, StringComparer.Ordinal)
            .First();
    }
}

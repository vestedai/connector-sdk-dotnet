using System.Security.Cryptography;
using System.Text;

namespace VestedAI.ConnectorSdk.Schema;

/// <summary>
/// Builds the canonical model for Microsoft SQL Server hosting Business
/// Central / LS Central.
/// </summary>
/// <remarks>
/// Deliberately not <c>sealed</c>. A connector declares its relational source
/// with <c>[RelationalSource]</c>, and that declaration names ITS OWN tool keys,
/// so it cannot live on this SDK-owned class. Subclassing is what lets a
/// connector annotate this provider without re-implementing it:
/// <code>
/// [RelationalSource(Engine = "sqlserver", DescribeTool = "erp_bc.describe_schema",
///                   QueryTool = "erp_bc.query_sql", SqlArg = "Sql")]
/// public sealed class BcSchemaProvider(ICatalogReader reader) : SqlServerProvider(reader);
/// </code>
/// ScopesAsync and DescribeAsync are virtual so a connector can extend or
/// replace scope handling without a fork; everything else is non-virtual, so a
/// subclass inherits the behaviour whole
/// and can change none of it — unsealing widens what can be declared, not what
/// can be overridden.
/// </remarks>
public class SqlServerProvider : IRelationalSchemaProvider
{
    private readonly ICatalogReader _reader;

    public SqlServerProvider(ICatalogReader reader) => _reader = reader;

    /// <summary>
    /// The scope key under which Business Central's own system tables are
    /// described — the ones with NO company prefix (<c>User</c>,
    /// <c>Object</c>, <c>Access Control</c> and about a hundred others).
    /// </summary>
    /// <remarks>
    /// They belong to no company, so before this existed they matched no scope
    /// and were dropped from every describe: measured on the live catalog,
    /// 0 of 16,250 extracted variants lacked a company prefix. Once the core's
    /// SQL gate moved to <c>enforce</c> that made any query touching them
    /// refusable as an unknown table, with no scope an operator could name to
    /// extract them.
    ///
    /// ⚠ The <c>$</c> makes a collision vanishingly unlikely but NOT
    /// impossible, and the difference matters. <see cref="BcPhysicalName"/>'s
    /// company group is non-greedy, which does not stop it expanding ACROSS a
    /// <c>$</c> when that is the only way the rest matches — so a company
    /// literally named <c>$system</c> would produce
    /// <c>$system$Item$&lt;app-id&gt;</c> and parse with that company. Rather
    /// than assume it cannot happen, <see cref="ScopesAsync"/> detects the
    /// clash and throws, because silently merging a real company's tables into
    /// the system scope would describe one company's data under a key that
    /// claims to hold none.
    /// </remarks>
    public const string SystemScopeKey = "$system";

    /// <summary>
    /// Table names BC uses for its own storage internals, excluded from the
    /// system scope: they describe how the catalog is stored rather than
    /// anything a question could be asked about.
    /// </summary>
    private static bool IsBcInternal(string tableName) =>
        tableName.StartsWith("$", StringComparison.Ordinal);

    public virtual async Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
    {
        var tables = await _reader.TablesAsync(ct).ConfigureAwait(false);

        var companies = tables
            .Select(t => BcPhysicalName.TryParse(t.Name, out var p) ? p.Company : null)
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        // Only offered when the catalog actually holds such tables, so a
        // source with none does not advertise an empty scope that would
        // extract to nothing and be refused by the core's ingestor.
        // Loud, not silent: see SystemScopeKey's remarks. A company that
        // parses to the sentinel would otherwise have its tables described
        // under a scope key that is supposed to hold company-less ones.
        if (companies.Contains(SystemScopeKey, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"A Business Central company is named \"{SystemScopeKey}\", which collides with the " +
                "scope key this SDK uses for company-less system tables. Rename the company, or " +
                "override ScopesAsync/DescribeAsync in your provider to choose a different sentinel.");
        }

        if (tables.Any(t => IsSystemTable(t.Name)))
        {
            companies.Add(SystemScopeKey);
        }

        return companies;
    }

    /// <summary>
    /// A table that belongs to no company: it does not parse as
    /// <c>Company$Logical$AppId</c> and is not one of BC's storage internals.
    /// </summary>
    private static bool IsSystemTable(string tableName) =>
        !BcPhysicalName.TryParse(tableName, out _) && !IsBcInternal(tableName);

    /// <summary>
    /// A cheap hash of the source catalog's shape.
    /// </summary>
    /// <remarks>
    /// Hashes sorted <c>"{schema}.{table}|{columnCount}"</c> entries, not just
    /// table names. A BC extension deploy typically ADDS A FIELD to an
    /// existing table rather than adding a new table, so a name-only hash
    /// would stay unchanged and the core would never re-extract — the
    /// snapshot goes silently stale, which is exactly the
    /// confidently-wrong-SQL failure this layer exists to prevent. Including
    /// the column count catches tables added/removed AND fields
    /// added/removed. It does NOT catch a same-shape type change (e.g.
    /// <c>nvarchar(50)</c> → <c>nvarchar(100)</c>) — the column count is
    /// unchanged, so the hash is unchanged. That gap is accepted here; the
    /// nightly full re-extract covers it. The sort is load-bearing: without
    /// it, an unordered <c>INFORMATION_SCHEMA</c> scan would re-hash
    /// differently on every poll and force a full re-extract every time.
    /// </remarks>
    public async Task<string> CatalogFingerprintAsync(CancellationToken ct)
    {
        var tables = await _reader.TablesAsync(ct).ConfigureAwait(false);
        var columns = await _reader.ColumnsAsync(ct).ConfigureAwait(false);

        var columnCountByTable = columns
            .GroupBy(c => c.TableName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var joined = string.Join('\n', tables
            .Select(t => $"{t.Schema}.{t.Name}|{(columnCountByTable.TryGetValue(t.Name, out var n) ? n : 0)}")
            .OrderBy(s => s, StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    public virtual async Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
    {
        var tables = await _reader.TablesAsync(ct).ConfigureAwait(false);
        var columns = await _reader.ColumnsAsync(ct).ConfigureAwait(false);
        var links = await _reader.ExtensionLinksAsync(ct).ConfigureAwait(false);

        var columnsByTable = columns
            .GroupBy(c => c.TableName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        if (string.Equals(scopeKey, SystemScopeKey, StringComparison.Ordinal))
        {
            return DescribeSystemScope(tables, columnsByTable);
        }

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

            // Empty for single-variant entities (canonical store contract,
            // 2026_08_12_000100_create_schema_snapshot_tables.php): a
            // single-variant entity has nothing to stitch together, so a
            // populated join key there would name a column with no join to
            // perform. The primary key itself is not lost — it is still
            // discoverable per-variant via CanonicalColumn.IsPk.
            var joinKey = variants.Count > 1 && columnsByTable.TryGetValue(basePhysical, out var baseCols)
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

    /// <summary>
    /// Describes Business Central's company-less system tables as one scope.
    /// </summary>
    /// <remarks>
    /// Deliberately simpler than the company path, because the shape is
    /// simpler: a system table has no <c>Company$Logical$AppId</c> structure,
    /// so there is no variant set to stitch, no extension to fold into a base,
    /// and therefore no join key. Each table is one entity with one variant
    /// carrying its literal name — which is exactly what a caller must write
    /// in SQL, since these tables are referenced unprefixed.
    ///
    /// Relations are empty for the same reason: variant_join rows describe how
    /// an entity's own physical tables stitch together, and here there is only
    /// ever one.
    /// </remarks>
    private static CanonicalSchema DescribeSystemScope(
        IReadOnlyList<CatalogTable> tables,
        IReadOnlyDictionary<string, List<CatalogColumn>> columnsByTable)
    {
        var entities = new List<CanonicalEntity>();

        foreach (var table in tables
                     .Where(t => IsSystemTable(t.Name))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var variants = new List<CanonicalVariant>
            {
                new(table.Name, "base", 0),
            };

            var entityColumns = new List<CanonicalColumn>();
            if (columnsByTable.TryGetValue(table.Name, out var cols))
            {
                foreach (var c in cols.OrderBy(c => c.OrdinalPosition))
                {
                    entityColumns.Add(new CanonicalColumn(
                        Name: c.ColumnName,
                        Type: c.DataType,
                        Nullable: c.IsNullable,
                        Position: c.OrdinalPosition,
                        IsPk: c.IsPrimaryKey,
                        Caption: null,
                        VariantPhysicalName: table.Name));
                }
            }

            entities.Add(new CanonicalEntity(
                LogicalName: table.Name,
                ScopeKey: SystemScopeKey,
                Kind: "table",
                Comment: null,
                JoinKey: new List<string>(),
                Variants: variants,
                Columns: entityColumns));
        }

        return new CanonicalSchema(entities, new List<CanonicalRelation>());
    }
}

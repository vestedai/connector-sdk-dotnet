namespace VestedAI.ConnectorSdk.Schema;

/// <summary>
/// Turns one engine's catalog into the canonical model. Engine specifics live
/// here and nowhere above: the core sees entities with variant sets whether
/// the source is SQL Server table-extensions or Magento's EAV value tables.
/// </summary>
public interface IRelationalSchemaProvider
{
    /// <summary>The scopes (BC companies, MySQL databases) this source exposes.</summary>
    Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct);

    Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct);

    /// <summary>
    /// A cheap hash of the source catalog, reported on Register. The core
    /// re-extracts only when this changes, so a connector that knows its
    /// database is unchanged costs the platform nothing.
    /// </summary>
    Task<string> CatalogFingerprintAsync(CancellationToken ct);
}

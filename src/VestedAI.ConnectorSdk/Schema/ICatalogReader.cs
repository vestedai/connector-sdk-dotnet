namespace VestedAI.ConnectorSdk.Schema;

public record CatalogTable(string Schema, string Name);

public record CatalogColumn(
    string TableName,
    string ColumnName,
    string DataType,
    bool IsNullable,
    int OrdinalPosition,
    bool IsPrimaryKey);

/// <summary>Maps an extension table to the base table it extends.</summary>
public record CatalogExtensionLink(string ExtensionTable, string BaseTable);

/// <summary>
/// The raw catalog rows a provider needs, behind a seam so the provider's
/// grouping logic is testable without a live database. The 26,191-row shape
/// of the real catalog is what makes fixture-driven tests worth having.
/// </summary>
public interface ICatalogReader
{
    Task<IReadOnlyList<CatalogTable>> TablesAsync(CancellationToken ct);

    Task<IReadOnlyList<CatalogColumn>> ColumnsAsync(CancellationToken ct);

    /// <summary>
    /// Extension-to-base links from BC's own metadata
    /// (<c>$ndo$navapptableextension</c>). Empty when that table is not
    /// readable, which is a documented fallback, not an error.
    /// </summary>
    Task<IReadOnlyList<CatalogExtensionLink>> ExtensionLinksAsync(CancellationToken ct);
}

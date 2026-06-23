namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Opaque pagination cursor passed into <see cref="PaginatedToolHandler{TArgs,TRow}.FetchPageAsync"/>.
/// <para><see cref="Token"/> is <c>null</c> on the first page and equal to
/// <see cref="DatasetPage{TRow}.NextCursor"/> from the previous page on subsequent pages.</para>
/// </summary>
public sealed class DatasetCursor
{
    /// <summary>Opaque continuation token from the previous page, or <c>null</c> for the first page.</summary>
    public string? Token { get; init; }

    /// <summary>Requested maximum number of rows per page. 0 means use the handler default.</summary>
    public int PageSize { get; init; }
}

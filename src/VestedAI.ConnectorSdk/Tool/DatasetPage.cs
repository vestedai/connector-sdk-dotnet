namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// A single page of rows returned by <see cref="PaginatedToolHandler{TArgs,TRow}.FetchPageAsync"/>.
/// </summary>
/// <typeparam name="TRow">Strongly-typed row POCO.</typeparam>
public sealed class DatasetPage<TRow>
{
    /// <summary>Rows for this page. Never <c>null</c>; may be empty on the final page.</summary>
    public required IReadOnlyList<TRow> Rows { get; init; }

    /// <summary>
    /// Opaque token that the caller passes as <see cref="DatasetCursor.Token"/> to fetch the next page.
    /// <c>null</c> indicates this is the last page.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>Total number of matching rows across all pages, if known. <c>null</c> when unknown.</summary>
    public long? Total { get; init; }
}

/// <summary>
/// Type-erased page used by the dispatcher so it can handle any
/// <see cref="PaginatedToolHandler{TArgs,TRow}"/> without knowing <c>TRow</c> at compile time.
/// </summary>
public sealed class BoxedPage
{
    /// <summary>
    /// The <see cref="DatasetPage{TRow}.Rows"/> value boxed as <see cref="object"/>.
    /// Callers should cast to <see cref="System.Collections.Generic.IReadOnlyList{T}"/> of the concrete row type.
    /// </summary>
    public required object Rows { get; init; }

    /// <summary>Opaque continuation token; <c>null</c> on the last page.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total row count across all pages, if known.</summary>
    public long? Total { get; init; }
}

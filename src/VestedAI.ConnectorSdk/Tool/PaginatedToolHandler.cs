namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Strongly-typed base class for paginated (dataset) tool handlers.
/// Implement <see cref="FetchPageAsync"/> to return one page of rows at a time.
/// </summary>
/// <typeparam name="TArgs">POCO args type. Properties decorated with
/// <c>[System.ComponentModel.Description]</c> flow their descriptions into the generated JSON Schema.</typeparam>
/// <typeparam name="TRow">POCO row type for a single result row.</typeparam>
/// <remarks>
/// The dispatcher calls <see cref="InvokePagedBoxedAsync"/> (inherited from
/// <see cref="ToolHandlerBase"/>). Single-style invocation via
/// <see cref="InvokeBoxedAsync"/> is not supported for paginated tools and throws
/// <see cref="NotSupportedException"/>.
/// </remarks>
public abstract class PaginatedToolHandler<TArgs, TRow> : ToolHandlerBase
{
    /// <inheritdoc/>
    public override Type ArgsType => typeof(TArgs);

    /// <inheritdoc/>
    public override Type ResultType => typeof(TRow);

    /// <summary>
    /// Fetch a single page of rows.
    /// </summary>
    /// <param name="args">Strongly-typed query arguments.</param>
    /// <param name="cursor">Pagination cursor (token from the previous page, or default for the first page).</param>
    /// <param name="ctx">Per-invocation context (org, run, user, etc.).</param>
    public abstract Task<DatasetPage<TRow>> FetchPageAsync(TArgs args, DatasetCursor cursor, ToolContext ctx);

    /// <summary>
    /// Not supported on paginated tools. Use <see cref="InvokePagedBoxedAsync"/> instead.
    /// </summary>
    public override Task<object> InvokeBoxedAsync(object args, ToolContext ctx)
        => throw new NotSupportedException("Paginated tool: use InvokePagedBoxedAsync.");

    /// <inheritdoc/>
    public override async Task<BoxedPage> InvokePagedBoxedAsync(object args, DatasetCursor cursor, ToolContext ctx)
    {
        var page = await FetchPageAsync((TArgs)args, cursor, ctx).ConfigureAwait(false);
        return new BoxedPage { Rows = page.Rows, NextCursor = page.NextCursor, Total = page.Total };
    }
}

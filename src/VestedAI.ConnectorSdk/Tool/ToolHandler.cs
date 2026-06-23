namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Non-generic base for the tool-handler registry and dispatcher.
/// Provides type-erased access to args/result types and boxed invocation.
/// </summary>
public abstract class ToolHandlerBase
{
    /// <summary>The strongly-typed args POCO type for this handler.</summary>
    public abstract Type ArgsType { get; }

    /// <summary>The strongly-typed result POCO type for this handler.</summary>
    public abstract Type ResultType { get; }

    /// <summary>
    /// Invoke the handler with boxed args, returning the boxed result.
    /// Used by the dispatcher after <see cref="ArgsValidation.Parse"/> has
    /// already deserialized the args to the correct POCO type.
    /// </summary>
    public abstract Task<object> InvokeBoxedAsync(object args, ToolContext ctx);

    /// <summary>
    /// Invoke the handler in paginated mode, returning a type-erased <see cref="BoxedPage"/>.
    /// Single-tool handlers (<see cref="ToolHandler{TArgs,TResult}"/>) throw
    /// <see cref="NotSupportedException"/> from this default implementation.
    /// <see cref="PaginatedToolHandler{TArgs,TRow}"/> overrides this method.
    /// </summary>
    public virtual Task<BoxedPage> InvokePagedBoxedAsync(object args, DatasetCursor cursor, ToolContext ctx)
        => throw new NotSupportedException("Tool is not paginated.");
}

/// <summary>
/// Strongly-typed base class for tool handlers.
/// Implement <see cref="HandleAsync"/> to process a tool call.
/// </summary>
/// <typeparam name="TArgs">POCO args type. Properties decorated with
/// <c>[System.ComponentModel.Description]</c> flow their descriptions into
/// the generated JSON Schema.</typeparam>
/// <typeparam name="TResult">POCO result type.</typeparam>
public abstract class ToolHandler<TArgs, TResult> : ToolHandlerBase
{
    /// <inheritdoc/>
    public override Type ArgsType => typeof(TArgs);

    /// <inheritdoc/>
    public override Type ResultType => typeof(TResult);

    /// <summary>Process a single tool-call invocation.</summary>
    public abstract Task<TResult> HandleAsync(TArgs args, ToolContext ctx);

    /// <inheritdoc/>
    public override async Task<object> InvokeBoxedAsync(object args, ToolContext ctx)
        => (await HandleAsync((TArgs)args, ctx))!;
}

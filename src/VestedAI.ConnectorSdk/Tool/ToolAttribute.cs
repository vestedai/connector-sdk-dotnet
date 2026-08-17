namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Marks a class as a Vested AI tool handler.
/// The decorated class must subclass <see cref="ToolHandler{TArgs, TResult}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>Dot-namespaced tool key (e.g. "myapp.orders.get").</summary>
    public string Key { get; set; } = "";

    /// <summary>Human-readable description shown to the LLM.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Optional display name. Defaults to <see cref="Key"/> when empty.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Connector-declared sensitivity hint. One of "read", "write", "destructive",
    /// "external_call", "medium". Empty (default) means unset — hub defaults to external_call.
    /// </summary>
    public string Sensitivity { get; set; } = "";

    /// <summary>Per-call timeout in milliseconds. Defaults to 30 000 ms.</summary>
    public int DefaultDeadlineMs { get; set; } = 30_000;

    /// <summary>Maximum serialised result size in bytes. Defaults to 1 MiB.</summary>
    public int MaxResultBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Agent keys this tool is bound to. Empty (the default) keeps the historical
    /// rule: the tool binds to the agent its key is namespaced under, and nothing
    /// changes for a connector that never sets this.
    ///
    /// A NON-EMPTY list is AUTHORITATIVE, not additive — the key's prefix confers
    /// nothing once a list is present, so a tool may live in one namespace and be
    /// callable only from another. Sharing one declaration across agents is the
    /// point: without it, the same behaviour needs a duplicate handler class per
    /// namespace.
    ///
    /// <see cref="ToolBinding.AllAgents"/> ("*") means every agent this connector
    /// declares, resolved at Register time, so an agent added later picks the tool
    /// up with no further change. It cannot be combined with explicit keys.
    ///
    /// Validated at <c>Build()</c>, before the worker dials the hub: an agent key
    /// this connector does not declare is refused, because it would otherwise bind
    /// the tool to nothing at all, silently.
    /// </summary>
    /// <example>
    /// <code>
    /// [Tool(Key = "erp_bc.data.run_sql", Description = "…",
    ///       Agents = new[] { "erp_bc.data", "erp_bc.retail" })]
    /// </code>
    /// </example>
    public string[] Agents { get; set; } = Array.Empty<string>();
}

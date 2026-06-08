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
}

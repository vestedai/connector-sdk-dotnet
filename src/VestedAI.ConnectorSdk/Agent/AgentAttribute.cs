namespace VestedAI.ConnectorSdk.Agent;

/// <summary>
/// Marks a class as a Vested AI agent declaration.
/// Apply once per agent class; the assembly scanner collects all such classes.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AgentAttribute : Attribute
{
    /// <summary>Dot-namespaced agent key (e.g. "myapp.orders").</summary>
    public string Key { get; set; } = "";

    /// <summary>Display name shown in the Vested AI UI.</summary>
    public string Name { get; set; } = "";

    /// <summary>LLM model identifier (e.g. "openai:gpt-4o").</summary>
    public string Model { get; set; } = "";

    /// <summary>Short human-readable description of what the agent does.</summary>
    public string Description { get; set; } = "";

    /// <summary>Agent lifecycle status. Defaults to "active".</summary>
    public string Status { get; set; } = "active";
}

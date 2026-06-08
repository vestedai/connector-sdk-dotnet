namespace VestedAI.ConnectorSdk.Agent;

/// <summary>
/// Adds a system/prompt instruction to an agent class.
/// Multiple instructions can be applied; they are sorted by <see cref="Position"/> at registration time.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class InstructionAttribute : Attribute
{
    /// <summary>Instruction type (e.g. "system", "user").</summary>
    public string Type { get; set; } = "";

    /// <summary>Sort order among multiple instructions (lower = first).</summary>
    public int Position { get; set; }

    /// <summary>The instruction text body.</summary>
    public string Body { get; set; } = "";

    /// <summary>Body format. Defaults to "markdown".</summary>
    public string Format { get; set; } = "markdown";
}

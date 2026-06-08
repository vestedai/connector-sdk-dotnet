namespace VestedAI.ConnectorSdk.Errors;

/// <summary>
/// Thrown when incoming tool-call args cannot be parsed or validated.
/// Carries the tool key so the dispatcher can build a precise error reply.
/// </summary>
public class ToolValidationException : ConnectorException
{
    /// <summary>The key of the tool whose args failed validation.</summary>
    public string ToolKey { get; }

    public ToolValidationException(string toolKey, string message)
        : base(message)
    {
        ToolKey = toolKey;
    }

    public ToolValidationException(string toolKey, string message, Exception inner)
        : base(message, inner)
    {
        ToolKey = toolKey;
    }
}

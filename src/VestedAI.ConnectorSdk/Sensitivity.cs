namespace VestedAI.ConnectorSdk;

/// <summary>
/// Canonical sensitivity values for tool declarations.
/// Empty string (the default) means "unset" — the hub defaults to external_call.
/// </summary>
public static class Sensitivity
{
    /// <summary>All valid tool sensitivity values.</summary>
    public static readonly string[] All = { "read", "write", "destructive", "external_call", "medium" };
}

namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Per-invocation context provided by the hub on each ToolCallRequest.
/// Use for tenant scoping, audit fields, and run/conversation correlation.
/// </summary>
public sealed record ToolContext(
    int OrgId,
    string AgentKey,
    string RunId,
    string ConversationId,
    string UserEmail = "",
    int UserId = 0);

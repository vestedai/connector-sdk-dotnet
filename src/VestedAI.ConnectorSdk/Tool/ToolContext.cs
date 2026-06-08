namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Per-invocation context provided by the hub on each ToolCallRequest.
/// Use for tenant scoping, audit fields, and run/conversation correlation.
/// </summary>
/// <remarks>
/// ERP-identity fields (<see cref="EmployeeNo"/>, <see cref="ErpIdentifier"/>,
/// <see cref="ErpDepartmentIdentifiers"/>) are init-only properties rather than
/// positional record parameters because C# 12 positional parameters cannot default
/// to collection literals. This keeps <c>TreatWarningsAsErrors</c> clean (no
/// <c>null!</c> suppression) while remaining backward-compatible for callers that
/// use positional or named construction with the existing six parameters.
/// </remarks>
public sealed record ToolContext(
    int OrgId,
    string AgentKey,
    string RunId,
    string ConversationId,
    string UserEmail = "",
    int UserId = 0)
{
    /// <summary>
    /// The caller's ERP employee number. Empty string when unset.
    /// Source: <c>ToolCallRequest.employee_no</c> (proto field 10).
    /// </summary>
    public string EmployeeNo { get; init; } = "";

    /// <summary>
    /// The caller's primary ERP identifier (e.g. SAP user ID). Empty string when unset.
    /// Source: <c>ToolCallRequest.erp_identifier</c> (proto field 11).
    /// </summary>
    public string ErpIdentifier { get; init; } = "";

    /// <summary>
    /// ERP identifiers of every department the caller belongs to.
    /// Empty list when unset. Never null.
    /// Source: <c>ToolCallRequest.erp_department_identifiers</c> (proto field 12, repeated).
    /// </summary>
    public IReadOnlyList<string> ErpDepartmentIdentifiers { get; init; } = Array.Empty<string>();
}

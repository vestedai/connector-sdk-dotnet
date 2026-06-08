namespace VestedAI.ConnectorSdk.Agent;

/// <summary>
/// Normalized representation of a single instruction, derived from <see cref="InstructionAttribute"/>.
/// </summary>
public sealed record InstructionDeclaration(
    string Type,
    int Position,
    string Body,
    string Format);

/// <summary>
/// Normalized agent declaration derived from <see cref="AgentAttribute"/> and
/// any <see cref="InstructionAttribute"/> instances on the same class.
/// </summary>
public sealed record AgentDeclaration(
    string Key,
    string Name,
    string Model,
    string Description,
    string Status,
    IReadOnlyList<InstructionDeclaration> Instructions);

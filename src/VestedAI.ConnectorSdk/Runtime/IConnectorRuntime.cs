using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Minimal contract the Daemon and Supervisor need from the app.
/// ConnectorApp (K-5) implements this interface.
/// </summary>
internal interface IConnectorRuntime
{
    IReadOnlyList<AgentDeclaration> Agents { get; }
    IReadOnlyDictionary<string, ToolDeclaration> Tools { get; }
}

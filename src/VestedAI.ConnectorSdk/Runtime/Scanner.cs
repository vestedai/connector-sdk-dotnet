using System.Reflection;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Walks an assembly and collects every class decorated with
/// <c>[Agent]</c> or <c>[Tool]</c>.
/// Port of the Node scanner.ts and Python scanner.py.
/// </summary>
public static class Scanner
{
    /// <summary>
    /// Scan <paramref name="asm"/> and return all agent + tool declarations.
    /// </summary>
    /// <returns>
    /// A tuple of:
    /// <list type="bullet">
    ///   <item><term>Agents</term><description>One entry per <c>[Agent]</c>-annotated type (deduped by key).</description></item>
    ///   <item><term>Tools</term><description>Keyed by tool key; duplicate key throws <see cref="ConnectorException"/>.</description></item>
    /// </list>
    /// </returns>
    /// <exception cref="ConnectorException">
    /// Thrown when two different types declare the same tool key.
    /// </exception>
    public static (IReadOnlyList<AgentDeclaration> Agents,
                   IReadOnlyDictionary<string, ToolDeclaration> Tools)
        ScanAssembly(Assembly asm)
    {
        var agents = new List<AgentDeclaration>();
        var seenAgentKeys = new HashSet<string>(StringComparer.Ordinal);
        var tools = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal);

        foreach (var type in asm.GetTypes())
        {
            // ---- [Agent] ----
            bool hasAgent = type.GetCustomAttributes(typeof(AgentAttribute), inherit: false).Length > 0;
            if (hasAgent)
            {
                var agentDecl = DeclarationFactory.FromAgentType(type);
                // Dedupe by key (a type re-exported from multiple modules may appear twice).
                if (seenAgentKeys.Add(agentDecl.Key))
                    agents.Add(agentDecl);
            }

            // ---- [Tool] ----
            bool hasTool = type.GetCustomAttributes(typeof(ToolAttribute), inherit: false).Length > 0;
            if (hasTool)
            {
                var toolDecl = DeclarationFactory.FromToolType(type);
                if (tools.TryGetValue(toolDecl.Key, out var existing))
                {
                    if (existing.HandlerType != type)
                    {
                        throw new ConnectorException(
                            $"Duplicate tool key \"{toolDecl.Key}\" registered by " +
                            $"{existing.HandlerType.FullName} and {type.FullName}.");
                    }
                    // Same type / same declaration — harmless, skip.
                }
                else
                {
                    tools[toolDecl.Key] = toolDecl;
                }
            }
        }

        return (agents.AsReadOnly(), tools);
    }
}

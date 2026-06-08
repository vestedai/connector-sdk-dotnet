using System.Reflection;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk;

/// <summary>
/// Factory for the <see cref="ConnectorHostBuilder"/> used to construct a <see cref="ConnectorApp"/>.
///
/// Typical usage in Program.cs:
/// <code>
/// return await ConnectorHost.CreateBuilder()
///     .ScanAssembly(Assembly.GetExecutingAssembly())
///     .Build()
///     .RunFromEnvironmentAsync();
/// </code>
/// </summary>
public static class ConnectorHost
{
    /// <summary>Creates a new <see cref="ConnectorHostBuilder"/>.</summary>
    public static ConnectorHostBuilder CreateBuilder() => new();
}

/// <summary>
/// Fluent builder that scans one or more assemblies for
/// <c>[Agent]</c> / <c>[Tool]</c> decorated types, validates the declarations,
/// and produces a <see cref="ConnectorApp"/>.
/// </summary>
public sealed class ConnectorHostBuilder
{
    private readonly List<AgentDeclaration> _agents = new();
    private readonly Dictionary<string, ToolDeclaration> _tools =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenAgentKeys =
        new(StringComparer.Ordinal);
    private bool _insecure;

    /// <summary>
    /// Scans <paramref name="asm"/> for <c>[Agent]</c> and <c>[Tool]</c> types and
    /// accumulates the resulting declarations.
    /// Multiple calls are allowed; duplicated tool keys across assemblies throw
    /// <see cref="ConnectorException"/>.
    /// Duplicate agent keys are silently deduped (same key, any assembly).
    /// </summary>
    public ConnectorHostBuilder ScanAssembly(Assembly asm)
    {
        var (agents, tools) = Scanner.ScanAssembly(asm);

        foreach (var agent in agents)
        {
            if (_seenAgentKeys.Add(agent.Key))
                _agents.Add(agent);
        }

        foreach (var (key, decl) in tools)
        {
            if (_tools.TryGetValue(key, out var existing))
            {
                // Same type registered twice (e.g. assembly loaded twice) — harmless.
                if (existing.HandlerType == decl.HandlerType)
                    continue;

                throw new ConnectorException(
                    $"Duplicate tool key \"{key}\" found across assemblies: " +
                    $"{existing.HandlerType.FullName} and {decl.HandlerType.FullName}.");
            }

            _tools[key] = decl;
        }

        return this;
    }

    /// <summary>
    /// Instructs the connector to connect to the hub over plain HTTP (no TLS).
    /// Only suitable for local development or trusted internal networks.
    /// </summary>
    public ConnectorHostBuilder UseInsecureTransport()
    {
        _insecure = true;
        return this;
    }

    /// <summary>
    /// Validates the accumulated declarations and returns a <see cref="ConnectorApp"/>.
    /// </summary>
    /// <exception cref="ConnectorException">
    /// Thrown when a tool key does not start with any registered agent key followed by a dot.
    /// </exception>
    public ConnectorApp Build()
    {
        var agentKeys = _agents.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        ValidateToolAgentPrefixes(agentKeys, _tools);

        return new ConnectorApp(
            _agents.AsReadOnly(),
            _tools,
            _insecure);
    }

    // ---------------------------------------------------------------------------
    // Internal test seam — lets tests exercise the validation logic without
    // needing a globally-visible "bad" fixture that would interfere with other tests.

    /// <summary>
    /// Runs the same tool-key/agent-key prefix validation that <see cref="Build"/> performs,
    /// but against caller-supplied declarations rather than the accumulated scanned ones.
    /// Only accessible from the test assembly via InternalsVisibleTo.
    /// </summary>
    internal static ConnectorApp BuildFromForTest(
        IReadOnlyList<AgentDeclaration> agents,
        IReadOnlyDictionary<string, ToolDeclaration> tools,
        bool insecure = false)
    {
        var agentKeys = agents.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        ValidateToolAgentPrefixes(agentKeys, tools);
        return new ConnectorApp(agents, tools, insecure);
    }

    // ---------------------------------------------------------------------------
    // Shared validation logic

    private static void ValidateToolAgentPrefixes(
        IReadOnlySet<string> agentKeys,
        IReadOnlyDictionary<string, ToolDeclaration> tools)
    {
        foreach (var toolKey in tools.Keys)
        {
            bool hasMatchingAgent = agentKeys.Any(
                agentKey => toolKey.StartsWith(agentKey + ".", StringComparison.Ordinal));

            if (!hasMatchingAgent)
            {
                throw new ConnectorException(
                    $"tool '{toolKey}' has no matching agent " +
                    $"(key must start with '<agentKey>.')");
            }
        }
    }
}

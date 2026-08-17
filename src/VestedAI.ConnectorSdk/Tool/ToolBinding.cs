using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;

namespace VestedAI.ConnectorSdk.Tool;

/// <summary>
/// Resolves which tools each agent gets.
/// </summary>
/// <remarks>
/// <para>
/// THE ONLY PLACE THIS IS DECIDED. Both the Register frame
/// (<c>Daemon.BuildRegisterAsync</c>) and the baseline fingerprint
/// (<c>Fingerprint.BuildCanonical</c>) call <see cref="Resolve"/>. Deriving
/// binding separately in each is how a fingerprint comes to disagree with the
/// frame it summarises — and the hub trusts the fingerprint to decide whether
/// to reconcile at all, so a disagreement means a registration whose content
/// changed gets short-circuited as unchanged. Nothing errors; the change simply
/// never happens.
/// </para>
/// <para>
/// The rule: an empty <see cref="ToolDeclaration.Agents"/> means the historical
/// namespace-prefix binding. A non-empty one is AUTHORITATIVE — the prefix
/// confers nothing once a list is present.
/// </para>
/// </remarks>
public static class ToolBinding
{
    /// <summary>Binds to every agent this connector declares.</summary>
    public const string AllAgents = "*";

    /// <summary>
    /// agent key -> its tools. Both the outer map and each list are ordinally
    /// sorted, because the fingerprint hashes the result and ordinal is the only
    /// ordering the other SDKs agree on.
    /// </summary>
    public static SortedDictionary<string, List<ToolDeclaration>> Resolve(
        IReadOnlyList<AgentDeclaration> agents,
        IReadOnlyDictionary<string, ToolDeclaration> tools)
    {
        var bound = new SortedDictionary<string, List<ToolDeclaration>>(StringComparer.Ordinal);
        foreach (var a in agents) bound[a.Key] = new List<ToolDeclaration>();

        foreach (var tool in tools.Values)
        {
            foreach (var agentKey in TargetsFor(tool, agents))
            {
                if (bound.TryGetValue(agentKey, out var list)) list.Add(tool);
            }
        }

        foreach (var list in bound.Values)
            list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));

        return bound;
    }

    /// <summary>
    /// Which agents one tool targets: the prefix rule when it names none,
    /// every declared agent for "*", otherwise the list verbatim.
    /// </summary>
    private static IEnumerable<string> TargetsFor(
        ToolDeclaration tool, IReadOnlyList<AgentDeclaration> agents)
    {
        if (tool.Agents.Count == 0)
        {
            foreach (var a in agents)
                if (tool.Key.StartsWith(a.Key + ".", StringComparison.Ordinal))
                    yield return a.Key;
            yield break;
        }

        if (tool.Agents.Contains(AllAgents))
        {
            foreach (var a in agents) yield return a.Key;
            yield break;
        }

        foreach (var k in tool.Agents) yield return k;
    }

    /// <summary>
    /// Refuses what cannot be meant; warns about what is legal but surprising.
    /// Called from <c>Build()</c>, before the worker dials the hub — the same
    /// seam where <c>[Credential]</c> and <c>[RelationalSource]</c> already fail.
    /// </summary>
    /// <param name="warn">
    /// Receives human-readable warnings. Separate from throwing so the caller
    /// routes them to its own logger, and so tests can assert on them.
    /// </param>
    public static void Validate(
        IReadOnlyList<AgentDeclaration> agents,
        IReadOnlyDictionary<string, ToolDeclaration> tools,
        Action<string> warn)
    {
        var declared = new HashSet<string>(agents.Select(a => a.Key), StringComparer.Ordinal);
        var known = string.Join(", ", declared.OrderBy(x => x, StringComparer.Ordinal));

        foreach (var tool in tools.Values)
        {
            if (tool.Agents.Count == 0)
            {
                // No list, so the prefix must find an agent — otherwise nothing
                // could ever call this tool, which is never intentional. This is
                // the guard ConnectorHostBuilder used to apply to EVERY tool;
                // it now applies only to those that name no agents, because a
                // tool that names its agents is legitimately allowed to sit
                // outside all of their namespaces.
                var prefixed = agents.Any(
                    a => tool.Key.StartsWith(a.Key + ".", StringComparison.Ordinal));

                if (!prefixed)
                {
                    throw new ConnectorException(
                        $"tool '{tool.Key}' has no matching agent (key must start with " +
                        $"'<agentKey>.'), and declares no Agents to bind it explicitly. " +
                        $"Declared agents: {known}.");
                }

                continue;
            }

            var hasStar = tool.Agents.Contains(AllAgents);
            if (hasStar && tool.Agents.Count > 1)
            {
                var explicitKeys = string.Join(", ", tool.Agents.Where(a => a != AllAgents));
                throw new ConnectorException(
                    $"[Tool(\"{tool.Key}\")] combines \"{AllAgents}\" with explicit agent " +
                    $"keys ({explicitKeys}). \"{AllAgents}\" already means every agent; " +
                    $"drop one or the other.");
            }

            if (hasStar) continue;

            foreach (var k in tool.Agents)
            {
                if (!declared.Contains(k))
                {
                    throw new ConnectorException(
                        $"[Tool(\"{tool.Key}\")] names agent \"{k}\", which this connector " +
                        $"does not declare. Declared agents: {known}.");
                }
            }

            // Legal, and easy to reach by accident: the key says one agent owns
            // the tool while the list says that agent cannot call it. Warn rather
            // than throw — it is exactly how you express "lives here, callable
            // from there".
            foreach (var a in agents)
            {
                if (tool.Key.StartsWith(a.Key + ".", StringComparison.Ordinal)
                    && !tool.Agents.Contains(a.Key))
                {
                    warn($"{tool.Key} declares agents [{string.Join(", ", tool.Agents)}] and " +
                         $"is therefore NOT available to {a.Key}; rename the key or add it " +
                         $"to the list.");
                }
            }
        }
    }
}

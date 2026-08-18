using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Tool;

public class ToolBindingTests
{
    private static AgentDeclaration Agent(string key) => new(
        Key: key, Name: key, Model: "openai:gpt-4o", Description: "d",
        Status: "active", Instructions: Array.Empty<InstructionDeclaration>());

    private static ToolDeclaration Tool(string key, params string[] agents) => new()
    {
        Key = key, Name = key, Description = "d", Sensitivity = "read",
        DefaultDeadlineMs = 30_000, MaxResultBytes = 1_048_576,
        InputSchemaJson = "{}", HandlerType = typeof(object),
        ArgsType = typeof(object), ResultType = typeof(object),
        Agents = agents,
    };

    private static Dictionary<string, ToolDeclaration> Map(params ToolDeclaration[] t)
        => t.ToDictionary(x => x.Key, x => x, StringComparer.Ordinal);

    [Fact]
    public void NoAgentsField_FallsBackToNamespacePrefix()
    {
        var bound = ToolBinding.Resolve(
            new[] { Agent("erp.data"), Agent("erp.retail") },
            Map(Tool("erp.data.run_sql")));

        Assert.Equal(new[] { "erp.data.run_sql" }, bound["erp.data"].Select(t => t.Key));
        Assert.Empty(bound["erp.retail"]);
    }

    [Fact]
    public void ExplicitList_BindsToEachNamedAgent()
    {
        var bound = ToolBinding.Resolve(
            new[] { Agent("erp.data"), Agent("erp.retail") },
            Map(Tool("erp.data.run_sql", "erp.data", "erp.retail")));

        Assert.Single(bound["erp.data"]);
        Assert.Single(bound["erp.retail"]);
    }

    // The key names erp.data; the list names only erp.retail. The list wins.
    [Fact]
    public void ExplicitList_IsAuthoritative_NotAdditive()
    {
        var bound = ToolBinding.Resolve(
            new[] { Agent("erp.data"), Agent("erp.retail") },
            Map(Tool("erp.data.run_sql", "erp.retail")));

        Assert.Empty(bound["erp.data"]);
        Assert.Single(bound["erp.retail"]);
    }

    [Fact]
    public void Star_BindsToEveryDeclaredAgent()
    {
        var bound = ToolBinding.Resolve(
            new[] { Agent("erp.data"), Agent("erp.retail"), Agent("erp.sales") },
            Map(Tool("erp.shared.ping", "*")));

        Assert.All(bound.Values, v => Assert.Single(v));
    }

    [Fact]
    public void EmptyList_TreatedAsOmitted()
    {
        var bound = ToolBinding.Resolve(
            new[] { Agent("erp.data") },
            Map(Tool("erp.data.run_sql")));

        Assert.Single(bound["erp.data"]);
    }

    [Fact]
    public void BoundToolsAreOrdinallySorted()
    {
        var bound = ToolBinding.Resolve(
            new[] { Agent("erp.data") },
            Map(Tool("erp.data.b", "erp.data"),
                Tool("erp.data.A", "erp.data"),
                Tool("erp.data.a", "erp.data")));

        Assert.Equal(
            new[] { "erp.data.A", "erp.data.a", "erp.data.b" },
            bound["erp.data"].Select(t => t.Key));
    }

    [Fact]
    public void UnknownAgentKey_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(() => ToolBinding.Validate(
            new[] { Agent("erp.data") },
            Map(Tool("erp.data.run_sql", "erp.nope")),
            _ => { }));

        Assert.Contains("erp.nope", ex.Message);
    }

    [Fact]
    public void StarMixedWithExplicitKeys_Throws()
    {
        Assert.Throws<ConnectorException>(() => ToolBinding.Validate(
            new[] { Agent("erp.data") },
            Map(Tool("erp.data.run_sql", "*", "erp.data")),
            _ => { }));
    }

    [Fact]
    public void KeyPrefixNotInList_Warns()
    {
        var warnings = new List<string>();
        ToolBinding.Validate(
            new[] { Agent("erp.data"), Agent("erp.retail") },
            Map(Tool("erp.data.run_sql", "erp.retail")),
            warnings.Add);

        Assert.Contains(warnings, w => w.Contains("erp.data.run_sql") && w.Contains("erp.data"));
    }

    // A shared tool named outside every agent namespace is legal PRECISELY
    // because it names its agents. The old prefix guard rejected this shape.
    [Fact]
    public void ToolOutsideEveryAgentNamespace_IsLegalWhenItNamesAgents()
    {
        var agents = new[] { Agent("erp.data"), Agent("erp.retail") };
        var tools = Map(Tool("erp.shared.run_sql", "erp.data", "erp.retail"));

        ToolBinding.Validate(agents, tools, _ => { });

        var bound = ToolBinding.Resolve(agents, tools);
        Assert.Single(bound["erp.data"]);
        Assert.Single(bound["erp.retail"]);
    }

    // …and is still refused when it names none, because then nothing could
    // ever call it and that is never intentional.
    [Fact]
    public void ToolMatchingNoAgentAndNamingNone_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(() => ToolBinding.Validate(
            new[] { Agent("erp.data") },
            Map(Tool("erp.shared.orphan")),
            _ => { }));

        Assert.Contains("erp.shared.orphan", ex.Message);
    }

    // ───────────────── HUB LIMITS: max_tools_per_agent ─────────────────
    //
    // Learned the hard way on 2026-08-18. `Agents = ["*"]` on erp_bc's run_sql
    // pushed ONE agent from 30 tools to 31, one over that connector's limit, so
    // the hub rejected the whole Register. With no accepted Register the hub
    // holds no declaration for the connector, which makes BOTH the schema gate
    // and the credential gate refuse — 528 lookup_failed and 284
    // credential_refused over roughly an hour, reported as "try again shortly",
    // advice that could never work.
    //
    // The hub names the offender as `agents[5].tools` — an index into the wire
    // frame, which a developer then has to map back to an agent. These name it.

    [Fact]
    public void HubLimits_UnderTheLimit_DoesNotThrow()
    {
        var agents = new[] { Agent("erp.data") };
        var tools = Map(Tool("erp.data.a"), Tool("erp.data.b"));

        ToolBinding.ValidateHubLimits(ToolBinding.Resolve(agents, tools), maxToolsPerAgent: 3);
    }

    [Fact]
    public void HubLimits_ExactlyAtTheLimit_DoesNotThrow()
    {
        // The hub refuses 31 against a limit of 30, so 30 itself is allowed.
        // Off-by-one here would ground a connector the hub accepts.
        var agents = new[] { Agent("erp.data") };
        var tools = Map(Tool("erp.data.a"), Tool("erp.data.b"), Tool("erp.data.c"));

        ToolBinding.ValidateHubLimits(ToolBinding.Resolve(agents, tools), maxToolsPerAgent: 3);
    }

    [Fact]
    public void HubLimits_OverTheLimit_ThrowsNamingTheAgentAndCounts()
    {
        var agents = new[] { Agent("erp.data"), Agent("erp.retail") };
        var tools = Map(
            Tool("erp.data.a"), Tool("erp.data.b"), Tool("erp.data.c"),
            Tool("erp.retail.x"));

        var ex = Assert.Throws<ConnectorException>(() => ToolBinding.ValidateHubLimits(
            ToolBinding.Resolve(agents, tools), maxToolsPerAgent: 2));

        // The agent by NAME, not an index, and both numbers.
        Assert.Contains("erp.data", ex.Message);
        Assert.Contains("3", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void HubLimits_NamesTheSharedToolWhenOneContributed()
    {
        // "*" is the declaration most likely to breach this, because it adds a
        // tool to EVERY agent including the fullest one. Saying so turns a
        // puzzling count into an obvious cause.
        var agents = new[] { Agent("erp.data"), Agent("erp.retail") };
        var tools = Map(
            Tool("erp.retail.a"), Tool("erp.retail.b"),
            Tool("erp.shared.run_sql", "*"));

        var ex = Assert.Throws<ConnectorException>(() => ToolBinding.ValidateHubLimits(
            ToolBinding.Resolve(agents, tools), maxToolsPerAgent: 2));

        Assert.Contains("erp.retail", ex.Message);
        Assert.Contains("erp.shared.run_sql", ex.Message);
    }

    [Fact]
    public void HubLimits_ZeroMeansUnknown_DoesNotThrow()
    {
        // proto3 uint32 defaults to 0, and an older hub sends no value at all.
        // Reading that as "the limit is zero" would ground every connector
        // against a hub that never set it — the failure mode this check exists
        // to prevent, inverted.
        var agents = new[] { Agent("erp.data") };
        var tools = Map(Tool("erp.data.a"), Tool("erp.data.b"));

        ToolBinding.ValidateHubLimits(ToolBinding.Resolve(agents, tools), maxToolsPerAgent: 0);
    }
}

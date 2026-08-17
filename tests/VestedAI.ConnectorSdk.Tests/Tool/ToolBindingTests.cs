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
}

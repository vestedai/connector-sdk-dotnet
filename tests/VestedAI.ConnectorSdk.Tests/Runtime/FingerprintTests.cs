using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

public class FingerprintTests
{
    // -----------------------------------------------------------------------
    // Helpers

    private static AgentDeclaration MakeAgent(string key = "crm.sales", string model = "openai:gpt-4o")
        => new AgentDeclaration(
            Key:          key,
            Name:         key,
            Model:        model,
            Description:  "test agent",
            Status:       "active",
            Instructions: Array.Empty<InstructionDeclaration>());

    private static AgentDeclaration MakeAgentWithInstruction(string key = "crm.sales")
        => new AgentDeclaration(
            Key:          key,
            Name:         key,
            Model:        "openai:gpt-4o",
            Description:  "agent with instructions",
            Status:       "active",
            Instructions: new[]
            {
                new InstructionDeclaration("system", 0, "You are helpful.", "markdown"),
                new InstructionDeclaration("user",   1, "Be concise.",     "markdown"),
            });

    private static ToolDeclaration MakeTool(
        string key = "crm.sales.lookup",
        string sensitivity = "read")
        => new ToolDeclaration
        {
            Key              = key,
            Name             = key,
            Description      = "lookup contact",
            Sensitivity      = sensitivity,
            DefaultDeadlineMs = 30_000,
            MaxResultBytes   = 1_048_576,
            InputSchemaJson  = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}",
            OutputSchemaJson = "{\"type\":\"object\"}",
            HandlerType      = typeof(object),
            ArgsType         = typeof(object),
            ResultType       = typeof(object),
        };

    // -----------------------------------------------------------------------
    // Tests

    [Fact]
    public void Fingerprint_IsNonEmpty64CharHex()
    {
        var agents = new[] { MakeAgent() };
        var tools  = new Dictionary<string, ToolDeclaration> { ["crm.sales.lookup"] = MakeTool() };
        var fp = Fingerprint.Compute(agents, tools);

        Assert.Equal(64, fp.Length);
        Assert.Matches("^[0-9a-f]{64}$", fp);
    }

    [Fact]
    public void Fingerprint_IsStable_AcrossTwoCalls()
    {
        var agents = new[] { MakeAgent() };
        var tools  = new Dictionary<string, ToolDeclaration> { ["crm.sales.lookup"] = MakeTool() };

        var fp1 = Fingerprint.Compute(agents, tools);
        var fp2 = Fingerprint.Compute(agents, tools);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_ChangesWhen_SensitivityChanges()
    {
        var agents = new[] { MakeAgent() };
        var toolRead  = new Dictionary<string, ToolDeclaration> { ["crm.sales.lookup"] = MakeTool(sensitivity: "read") };
        var toolWrite = new Dictionary<string, ToolDeclaration> { ["crm.sales.lookup"] = MakeTool(sensitivity: "write") };

        var fp1 = Fingerprint.Compute(agents, toolRead);
        var fp2 = Fingerprint.Compute(agents, toolWrite);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_ChangesWhen_ToolAdded()
    {
        var agents = new[] { MakeAgent() };
        var tools1 = new Dictionary<string, ToolDeclaration> { ["crm.sales.lookup"] = MakeTool() };
        var tools2 = new Dictionary<string, ToolDeclaration>
        {
            ["crm.sales.lookup"] = MakeTool(),
            ["crm.sales.update"] = MakeTool("crm.sales.update", "write"),
        };

        var fp1 = Fingerprint.Compute(agents, tools1);
        var fp2 = Fingerprint.Compute(agents, tools2);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_ChangesWhen_AgentAdded()
    {
        var agents1 = new[] { MakeAgent("crm.sales") };
        var agents2 = new[] { MakeAgent("crm.sales"), MakeAgent("crm.analytics") };
        var tools = new Dictionary<string, ToolDeclaration>();

        var fp1 = Fingerprint.Compute(agents1, tools);
        var fp2 = Fingerprint.Compute(agents2, tools);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_ChangesWhen_AgentDescriptionChanges()
    {
        var agent1 = new AgentDeclaration("crm.sales", "Sales", "openai:gpt-4o", "desc A", "active", Array.Empty<InstructionDeclaration>());
        var agent2 = new AgentDeclaration("crm.sales", "Sales", "openai:gpt-4o", "desc B", "active", Array.Empty<InstructionDeclaration>());
        var tools = new Dictionary<string, ToolDeclaration>();

        var fp1 = Fingerprint.Compute(new[] { agent1 }, tools);
        var fp2 = Fingerprint.Compute(new[] { agent2 }, tools);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_IsOrderIndependent_ForAgents()
    {
        // Agents sorted by key — regardless of input order.
        var agentA = MakeAgent("crm.analytics");
        var agentB = MakeAgent("crm.sales");
        var tools = new Dictionary<string, ToolDeclaration>();

        var fp1 = Fingerprint.Compute(new[] { agentA, agentB }, tools);
        var fp2 = Fingerprint.Compute(new[] { agentB, agentA }, tools);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_IsOrderIndependent_ForTools()
    {
        var agents = new[] { MakeAgent() };
        var tool1 = MakeTool("crm.sales.lookup", "read");
        var tool2 = MakeTool("crm.sales.update", "write");

        var tools1 = new Dictionary<string, ToolDeclaration>
        {
            ["crm.sales.lookup"] = tool1,
            ["crm.sales.update"] = tool2,
        };
        var tools2 = new Dictionary<string, ToolDeclaration>
        {
            ["crm.sales.update"] = tool2,
            ["crm.sales.lookup"] = tool1,
        };

        var fp1 = Fingerprint.Compute(agents, tools1);
        var fp2 = Fingerprint.Compute(agents, tools2);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_WithInstructions_IsStable()
    {
        var agents = new[] { MakeAgentWithInstruction() };
        var tools  = new Dictionary<string, ToolDeclaration> { ["crm.sales.lookup"] = MakeTool() };

        var fp1 = Fingerprint.Compute(agents, tools);
        var fp2 = Fingerprint.Compute(agents, tools);

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void Fingerprint_EmptyAgentsAndTools_IsNonEmpty()
    {
        var fp = Fingerprint.Compute(
            Array.Empty<AgentDeclaration>(),
            new Dictionary<string, ToolDeclaration>());
        Assert.Equal(64, fp.Length);
        Assert.NotEqual("", fp);
    }
}

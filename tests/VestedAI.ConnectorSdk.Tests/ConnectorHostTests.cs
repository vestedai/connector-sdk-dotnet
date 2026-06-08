using System.Reflection;
using VestedAI.ConnectorSdk;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Tool;
using VestedAI.ConnectorSdk.Tests.Runtime;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// ---------------------------------------------------------------------------
// K-5 test fixtures — dedicated namespace so they don't bleed into ScannerTests
// ---------------------------------------------------------------------------

/// <summary>
/// A valid agent+tool pair that satisfies the prefix validation rule.
/// Agent key "k5.demo" + tool key "k5.demo.ping" — the tool starts with the agent key + ".".
/// </summary>
[Agent(Key = "k5.demo", Name = "K5DemoAgent", Model = "openai:gpt-4o",
       Description = "Demo agent for K-5 tests.")]
public class K5DemoAgent { }

[Tool(Key = "k5.demo.ping", Description = "Ping tool for K-5 tests.", Sensitivity = "read")]
public class K5DemoPingTool : ToolHandler<K5DemoPingTool.Args, K5DemoPingTool.Result>
{
    public class Args { public string Message { get; set; } = ""; }
    public class Result { public string Reply { get; set; } = ""; }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { Reply = $"pong: {args.Message}" });
}

// ---------------------------------------------------------------------------
// Tests — ConnectorHostBuilder (K-5)
// ---------------------------------------------------------------------------

public class ConnectorHostTests
{
    // A hermetic FakeAssembly that exposes exactly the K-5 demo types.
    private static readonly FakeAssembly _k5Assembly = new FakeAssembly(
        typeof(K5DemoAgent),
        typeof(K5DemoPingTool));

    // -----------------------------------------------------------------------
    // Build() happy path

    [Fact]
    public void CreateBuilder_ScanAssembly_Build_Succeeds()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_k5Assembly)
            .Build();

        Assert.NotNull(app);
    }

    [Fact]
    public void Build_Agents_ContainsK5DemoAgent()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_k5Assembly)
            .Build();

        var keys = app.Agents.Select(a => a.Key).ToHashSet();
        Assert.Contains("k5.demo", keys);
    }

    [Fact]
    public void Build_Tools_ContainsK5DemoPing()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_k5Assembly)
            .Build();

        Assert.True(app.Tools.ContainsKey("k5.demo.ping"));
    }

    [Fact]
    public void Build_AgentCount_IsOne()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_k5Assembly)
            .Build();

        Assert.Single(app.Agents);
    }

    [Fact]
    public void Build_ToolCount_IsOne()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_k5Assembly)
            .Build();

        Assert.Single(app.Tools);
    }

    [Fact]
    public void Build_WithScannerFixtures_FindsBothAgents()
    {
        // Reuse the hermetic FakeAssembly already defined in ScannerTests.
        var asm = new FakeAssembly(
            typeof(ScannerSalesAgent),
            typeof(ScannerSupportAgent),
            typeof(ScannerLookupTool),
            typeof(ScannerCreateTicketTool));

        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(asm)
            .Build();

        var keys = app.Agents.Select(a => a.Key).ToHashSet();
        Assert.Contains("scan.sales", keys);
        Assert.Contains("scan.support", keys);
    }

    [Fact]
    public void Build_WithScannerFixtures_FindsBothTools()
    {
        var asm = new FakeAssembly(
            typeof(ScannerSalesAgent),
            typeof(ScannerSupportAgent),
            typeof(ScannerLookupTool),
            typeof(ScannerCreateTicketTool));

        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(asm)
            .Build();

        Assert.True(app.Tools.ContainsKey("scan.sales.lookup"));
        Assert.True(app.Tools.ContainsKey("scan.support.ticket"));
    }

    // -----------------------------------------------------------------------
    // ScanAssembly — multiple calls accumulate (deduplication)

    [Fact]
    public void ScanAssembly_CalledTwice_SameTypes_DoesNotDuplicate()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_k5Assembly)
            .ScanAssembly(_k5Assembly)
            .Build();

        Assert.Single(app.Agents);
        Assert.Single(app.Tools);
    }

    // -----------------------------------------------------------------------
    // UseInsecureTransport — merely exercises the fluent API (no visible state check)

    [Fact]
    public void UseInsecureTransport_BuildSucceeds()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_k5Assembly)
            .UseInsecureTransport()
            .Build();

        Assert.NotNull(app);
    }

    // -----------------------------------------------------------------------
    // Build() prefix validation — hermetic via the internal test seam

    [Fact]
    public void Build_ToolKeyMissingAgentPrefix_ThrowsConnectorException()
    {
        // An agent with key "x.agent" and a tool with key "y.something.else" —
        // the tool key does NOT start with "x.agent." so Build() must throw.
        var agentDecl = DeclarationFactory.FromAgentType(typeof(K5DemoAgent));
        // Override key by building a standalone decl through the real agent but
        // pair it with a tool whose key has no matching prefix.
        var toolDecl = DeclarationFactory.FromToolType(typeof(K5DemoPingTool));

        // We construct a scenario where the agent key is "k5.demo" but the
        // tool key is "unrelated.ping" (no agent with prefix "unrelated").
        // We do this via the internal seam BuildFromForTest so we don't need
        // a globally-visible invalid tool fixture that would break scanner tests.

        // Build a minimal AgentDeclaration via an existing valid agent type.
        // Its key is "k5.demo".  The tool key we pass will be "unrelated.ping".
        var agentList = new List<AgentDeclaration> { agentDecl };

        // Fabricate a ToolDeclaration with a non-matching key by wrapping the real
        // K5DemoPingTool declaration in a fresh dict with a renamed key.
        var badToolDict = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal)
        {
            // "unrelated.ping" does not start with "k5.demo."
            ["unrelated.ping"] = toolDecl
        };

        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHostBuilder.BuildFromForTest(agentList, badToolDict));

        Assert.Contains("unrelated.ping", ex.Message);
        Assert.Contains("no matching agent", ex.Message);
    }

    [Fact]
    public void Build_ToolKeyMatchesAgentPrefix_DoesNotThrow()
    {
        var agentDecl = DeclarationFactory.FromAgentType(typeof(K5DemoAgent));
        var toolDecl  = DeclarationFactory.FromToolType(typeof(K5DemoPingTool));

        // "k5.demo.ping" starts with "k5.demo." — valid.
        var goodToolDict = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal)
        {
            [toolDecl.Key] = toolDecl
        };

        var app = ConnectorHostBuilder.BuildFromForTest(
            new List<AgentDeclaration> { agentDecl },
            goodToolDict);

        Assert.NotNull(app);
    }

    // -----------------------------------------------------------------------
    // ParseHub — unit tests (ConnectorApp.ParseHub is internal)

    [Theory]
    [InlineData("hub.example.com:4443", "hub.example.com", 4443)]
    [InlineData("hub.example.com:9000", "hub.example.com", 9000)]
    [InlineData("hub.example.com",      "hub.example.com", 4443)]   // no colon → default 4443
    [InlineData("localhost:50051",       "localhost",       50051)]
    [InlineData("127.0.0.1:1234",        "127.0.0.1",       1234)]
    [InlineData("hub.example.com:notanumber", "hub.example.com", 4443)]  // bad port → default
    public void ParseHub_ParsesCorrectly(string hub, string expectedHost, int expectedPort)
    {
        var (host, port) = ConnectorApp.ParseHub(hub);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }
}

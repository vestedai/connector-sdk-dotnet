using System.Reflection;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

// ---------------------------------------------------------------------------
// Scanner fixture types — decorated with [Agent] / [Tool]
// ---------------------------------------------------------------------------

[Agent(Key = "scan.sales", Name = "SalesAgent", Model = "openai:gpt-4o")]
public class ScannerSalesAgent { }

[Agent(Key = "scan.support", Name = "SupportAgent", Model = "openai:gpt-4o")]
[Instruction(Type = "system", Position = 0, Body = "You are a support agent.")]
public class ScannerSupportAgent { }

[Tool(Key = "scan.sales.lookup", Description = "Look up a contact", Sensitivity = "read")]
public class ScannerLookupTool : ToolHandler<ScannerLookupTool.Args, ScannerLookupTool.Result>
{
    public class Args { public string ContactId { get; set; } = ""; }
    public class Result { public string Name { get; set; } = ""; }
    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { Name = "stub" });
}

[Tool(Key = "scan.support.ticket", Description = "Create a ticket", Sensitivity = "write")]
public class ScannerCreateTicketTool : ToolHandler<ScannerCreateTicketTool.Args, ScannerCreateTicketTool.Result>
{
    public class Args { public string Subject { get; set; } = ""; }
    public class Result { public int TicketId { get; set; } }
    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { TicketId = 1 });
}

// ---------------------------------------------------------------------------
// Duplicate-key fixtures for the dupe test
// ---------------------------------------------------------------------------

[Tool(Key = "scan.dupe", Description = "First dupe")]
public class DupeToolA : ToolHandler<DupeToolA.Args, DupeToolA.Result>
{
    public class Args { public string X { get; set; } = ""; }
    public class Result { public string Y { get; set; } = ""; }
    public override Task<Result> HandleAsync(Args a, ToolContext c)
        => Task.FromResult(new Result { Y = a.X });
}

[Tool(Key = "scan.dupe", Description = "Second dupe")]
public class DupeToolB : ToolHandler<DupeToolB.Args, DupeToolB.Result>
{
    public class Args { public string X { get; set; } = ""; }
    public class Result { public string Y { get; set; } = ""; }
    public override Task<Result> HandleAsync(Args a, ToolContext c)
        => Task.FromResult(new Result { Y = a.X });
}

// ---------------------------------------------------------------------------
// Assembly proxy — controls exactly which types Scanner sees.
// ---------------------------------------------------------------------------

/// <summary>
/// Wraps an explicit type list so Scanner tests are hermetic and not affected
/// by other fixtures in the test assembly (e.g. BogusToolHandler with
/// Sensitivity="bogus", which is intentionally invalid for K-2 validation tests).
/// </summary>
internal sealed class FakeAssembly : Assembly
{
    private readonly Type[] _types;
    public FakeAssembly(params Type[] types) => _types = types;
    public override Type[] GetTypes() => _types;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class ScannerTests
{
    private static readonly FakeAssembly _normalAssembly = new FakeAssembly(
        typeof(ScannerSalesAgent),
        typeof(ScannerSupportAgent),
        typeof(ScannerLookupTool),
        typeof(ScannerCreateTicketTool));

    [Fact]
    public void ScanAssembly_FindsBothScannerAgents()
    {
        var (agents, _) = Scanner.ScanAssembly(_normalAssembly);

        var keys = agents.Select(a => a.Key).ToHashSet();
        Assert.Contains("scan.sales", keys);
        Assert.Contains("scan.support", keys);
    }

    [Fact]
    public void ScanAssembly_FindsBothScannerTools()
    {
        var (_, tools) = Scanner.ScanAssembly(_normalAssembly);

        Assert.True(tools.ContainsKey("scan.sales.lookup"), "scan.sales.lookup not found");
        Assert.True(tools.ContainsKey("scan.support.ticket"), "scan.support.ticket not found");
    }

    [Fact]
    public void ScanAssembly_AgentDeclaration_IsCorrect()
    {
        var (agents, _) = Scanner.ScanAssembly(_normalAssembly);

        var support = agents.First(a => a.Key == "scan.support");
        Assert.Equal("SupportAgent", support.Name);
        Assert.Single(support.Instructions);
        Assert.Equal("system", support.Instructions[0].Type);
        Assert.Equal("You are a support agent.", support.Instructions[0].Body);
    }

    [Fact]
    public void ScanAssembly_ToolDeclaration_IsCorrect()
    {
        var (_, tools) = Scanner.ScanAssembly(_normalAssembly);

        var lookup = tools["scan.sales.lookup"];
        Assert.Equal("Look up a contact", lookup.Description);
        Assert.Equal("read", lookup.Sensitivity);
    }

    [Fact]
    public void ScanAssembly_AgentCount_IsExact()
    {
        var (agents, _) = Scanner.ScanAssembly(_normalAssembly);
        // Only 2 agents in our hermetic assembly.
        Assert.Equal(2, agents.Count);
    }

    [Fact]
    public void ScanAssembly_ToolCount_IsExact()
    {
        var (_, tools) = Scanner.ScanAssembly(_normalAssembly);
        // Only 2 tools in our hermetic assembly.
        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public void ScanAssembly_NoAgentDuplicates_OnRepeatedScan()
    {
        // Running twice must not add duplicates.
        var (agents1, _) = Scanner.ScanAssembly(_normalAssembly);
        var (agents2, _) = Scanner.ScanAssembly(_normalAssembly);

        var keys1 = agents1.Select(a => a.Key).OrderBy(x => x).ToList();
        var keys2 = agents2.Select(a => a.Key).OrderBy(x => x).ToList();
        Assert.Equal(keys1, keys2);
    }

    [Fact]
    public void ScanAssembly_EmptyAssembly_ReturnsEmpty()
    {
        var (agents, tools) = Scanner.ScanAssembly(new FakeAssembly());
        Assert.Empty(agents);
        Assert.Empty(tools);
    }

    [Fact]
    public void ScanAssembly_TypeWithNoAttributes_IsIgnored()
    {
        // A plain class with no [Agent] or [Tool] should be skipped.
        var asm = new FakeAssembly(typeof(string), typeof(object), typeof(ScannerSalesAgent));
        var (agents, tools) = Scanner.ScanAssembly(asm);
        Assert.Single(agents);
        Assert.Empty(tools);
    }

    [Fact]
    public void ScanAssembly_DuplicateToolKey_ThrowsConnectorException()
    {
        var dupeAsm = new FakeAssembly(typeof(DupeToolA), typeof(DupeToolB));

        var ex = Assert.Throws<ConnectorException>(() => Scanner.ScanAssembly(dupeAsm));
        Assert.Contains("Duplicate", ex.Message);
        Assert.Contains("scan.dupe", ex.Message);
    }

    [Fact]
    public void ScanAssembly_DuplicateAgentKey_IsDeduped()
    {
        // Same agent type listed twice — scanner must dedupe by key, not throw.
        var asm = new FakeAssembly(typeof(ScannerSalesAgent), typeof(ScannerSalesAgent));
        var (agents, _) = Scanner.ScanAssembly(asm);
        Assert.Single(agents);
        Assert.Equal("scan.sales", agents[0].Key);
    }

    [Fact]
    public void ScanAssembly_ToolDeclaration_HandlerTypeIsCorrect()
    {
        var (_, tools) = Scanner.ScanAssembly(_normalAssembly);
        Assert.Equal(typeof(ScannerLookupTool), tools["scan.sales.lookup"].HandlerType);
    }
}

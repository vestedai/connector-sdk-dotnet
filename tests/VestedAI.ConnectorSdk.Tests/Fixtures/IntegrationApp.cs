using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk.Tests.Fixtures;

// ---------------------------------------------------------------------------
// Integration-test fixtures
// ---------------------------------------------------------------------------
// A minimal agent+tool pair used by IntegrationTests.cs.
// The tool declares Sensitivity="destructive" so the integration tests can
// assert that the sensitivity value correctly rode through Register → ToolDecl.
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal test agent for integration tests.
/// Key "t.test" scopes all integration test tools.
/// </summary>
[Agent(Key = "t.test", Name = "IntegrationTestAgent", Model = "openai:gpt-4o",
       Description = "Agent fixture for K-6 integration tests.")]
public class IntegrationTestAgent { }

/// <summary>
/// Echo tool: returns { "echoed": "&lt;text&gt;" } for input { "text": "&lt;value&gt;" }.
/// Declares Sensitivity="destructive" to verify sensitivity roundtrip.
/// </summary>
[Tool(Key = "t.test.echo",
      Description = "Echo the input text back as { echoed: text }.",
      Sensitivity = "destructive")]
public class IntegrationEchoTool : ToolHandler<IntegrationEchoTool.Args, IntegrationEchoTool.Result>
{
    public class Args
    {
        public string Text { get; set; } = "";
    }

    public class Result
    {
        public string Echoed { get; set; } = "";
    }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { Echoed = args.Text });
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Vested.V1;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

[Tool(Key = "disp.add", Description = "Add two ints", Sensitivity = "read")]
public class AddTool : ToolHandler<AddTool.Args, AddTool.Result>
{
    public class Args { public int A { get; set; } public int B { get; set; } }
    public class Result { public int Sum { get; set; } }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { Sum = args.A + args.B });
}

[Tool(Key = "disp.thrower", Description = "Always throws")]
public class ThrowingTool : ToolHandler<ThrowingTool.Args, ThrowingTool.Result>
{
    public class Args { public string Input { get; set; } = ""; }
    public class Result { public string Output { get; set; } = ""; }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => throw new InvalidOperationException("handler blew up");
}

[Tool(Key = "disp.slow", Description = "Simulates slow work")]
public class SlowTool : ToolHandler<SlowTool.Args, SlowTool.Result>
{
    public class Args { public int DelayMs { get; set; } }
    public class Result { public string Done { get; set; } = "yes"; }

    public override async Task<Result> HandleAsync(Args args, ToolContext ctx)
    {
        await Task.Delay(args.DelayMs);
        return new Result();
    }
}

// ---------------------------------------------------------------------------
// Dispatcher test helper
// ---------------------------------------------------------------------------

/// <summary>
/// Captures all ConnectorMsg sends in a thread-safe list.
/// </summary>
internal sealed class CapturingSend
{
    private readonly ConcurrentQueue<ConnectorMsg> _captured = new();
    public IReadOnlyCollection<ConnectorMsg> Captured => _captured;

    public Task SendAsync(ConnectorMsg msg)
    {
        _captured.Enqueue(msg);
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class DispatcherTests
{
    private static IReadOnlyDictionary<string, ToolDeclaration> BuildRegistry(params Type[] handlerTypes)
    {
        var dict = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal);
        foreach (var t in handlerTypes)
        {
            var decl = DeclarationFactory.FromToolType(t);
            dict[decl.Key] = decl;
        }
        return dict;
    }

    private static ToolCallRequest MakeRequest(string toolKey, string argsJson, string invocationId = "inv-1")
        => new ToolCallRequest
        {
            ToolKey      = toolKey,
            InvocationId = invocationId,
            ArgsJson     = ByteString.CopyFrom(Encoding.UTF8.GetBytes(argsJson)),
            OrganizationId = "42",
            AgentKey     = "a.test",
            ConversationId = "conv-1",
            UserId       = "7",
            UserEmail    = "test@example.com",
        };

    // -----------------------------------------------------------------------
    // Successful call

    [Fact]
    public async Task SuccessfulCall_ReturnsResultJson()
    {
        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(BuildRegistry(typeof(AddTool)), capture.SendAsync);

        dispatcher.Dispatch(MakeRequest("disp.add", "{\"a\":3,\"b\":4}"));

        // Wait for the task to complete.
        await WaitForMessages(capture, count: 1);

        var msg = capture.Captured.Single();
        Assert.NotNull(msg.ToolCallResponse);
        Assert.Equal("inv-1", msg.ToolCallResponse.InvocationId);
        // result_json should be set (oneof: ResultJson case)
        Assert.Equal(ToolCallResponse.ResultOneofCase.ResultJson, msg.ToolCallResponse.ResultCase);
        // Parse it and verify the sum.
        var json = msg.ToolCallResponse.ResultJson.ToStringUtf8();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(7, doc.RootElement.GetProperty("Sum").GetInt32());
    }

    // -----------------------------------------------------------------------
    // Context is built correctly

    [Fact]
    public async Task SuccessfulCall_PassesCorrectContextToHandler()
    {
        var capture = new CapturingSend();

        // We'll test via the EchoContextTool fixture defined below.
        var dict = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal);
        var decl = DeclarationFactory.FromToolType(typeof(EchoContextTool));
        dict[decl.Key] = decl;

        var dispatcher = new Dispatcher(dict, capture.SendAsync);
        dispatcher.Dispatch(new ToolCallRequest
        {
            ToolKey        = "disp.echo_ctx",
            InvocationId   = "ctx-1",
            ArgsJson       = ByteString.CopyFrom(Encoding.UTF8.GetBytes("{}")),
            OrganizationId = "99",
            AgentKey       = "a.sales",
            ConversationId = "conv-99",
            UserId         = "5",
            UserEmail      = "ctx@example.com",
        });

        await WaitForMessages(capture, count: 1);

        var resp = capture.Captured.Single().ToolCallResponse;
        Assert.Equal(ToolCallResponse.ResultOneofCase.ResultJson, resp.ResultCase);
        using var doc = JsonDocument.Parse(resp.ResultJson.ToStringUtf8());
        var root = doc.RootElement;
        Assert.Equal(99, root.GetProperty("OrgId").GetInt32());
        Assert.Equal("a.sales", root.GetProperty("AgentKey").GetString());
        Assert.Equal("conv-99", root.GetProperty("ConversationId").GetString());
        Assert.Equal(5, root.GetProperty("UserId").GetInt32());
        Assert.Equal("ctx@example.com", root.GetProperty("UserEmail").GetString());
    }

    // -----------------------------------------------------------------------
    // Unknown tool

    [Fact]
    public async Task UnknownTool_ErrorIs_UnknownTool()
    {
        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(BuildRegistry(typeof(AddTool)), capture.SendAsync);

        dispatcher.Dispatch(MakeRequest("no.such.tool", "{}", "inv-miss"));

        await WaitForMessages(capture, count: 1);

        var resp = capture.Captured.Single().ToolCallResponse;
        Assert.Equal("inv-miss", resp.InvocationId);
        Assert.Equal(ToolCallResponse.ResultOneofCase.Error, resp.ResultCase);
        Assert.Equal("unknown tool: no.such.tool", resp.Error);
        // No synthetic code prefix.
        Assert.DoesNotContain(":", resp.Error[0..7]);  // "unknown" starts it, no "ERR_"
    }

    // -----------------------------------------------------------------------
    // Invalid args

    [Fact]
    public async Task InvalidArgs_ErrorStartsWith_ToolCallInvalidArgs()
    {
        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(BuildRegistry(typeof(AddTool)), capture.SendAsync);

        dispatcher.Dispatch(MakeRequest("disp.add", "not-valid-json", "inv-bad"));

        await WaitForMessages(capture, count: 1);

        var resp = capture.Captured.Single().ToolCallResponse;
        Assert.Equal(ToolCallResponse.ResultOneofCase.Error, resp.ResultCase);
        Assert.StartsWith("tool_call_invalid_args:", resp.Error);
        // No extra code prefix like "ERR_VALIDATION:".
        Assert.False(resp.Error.StartsWith("ERR_"));
    }

    // -----------------------------------------------------------------------
    // Handler throws

    [Fact]
    public async Task HandlerThrows_ErrorIsExceptionMessage()
    {
        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(BuildRegistry(typeof(ThrowingTool)), capture.SendAsync);

        dispatcher.Dispatch(MakeRequest("disp.thrower", "{\"input\":\"x\"}", "inv-throw"));

        await WaitForMessages(capture, count: 1);

        var resp = capture.Captured.Single().ToolCallResponse;
        Assert.Equal(ToolCallResponse.ResultOneofCase.Error, resp.ResultCase);
        Assert.Equal("handler blew up", resp.Error);
        // No synthetic code prefix.
        Assert.False(resp.Error.StartsWith("ERR_"));
    }

    // -----------------------------------------------------------------------
    // Dispatch returns synchronously (handler not awaited by caller)

    [Fact]
    public void Dispatch_ReturnsSynchronously_BeforeHandlerCompletes()
    {
        // SlowTool sleeps 5 seconds — Dispatch must return immediately.
        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(BuildRegistry(typeof(SlowTool)), capture.SendAsync);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        dispatcher.Dispatch(MakeRequest("disp.slow", "{\"delayMs\":5000}"));
        sw.Stop();

        // Dispatch must return within 100 ms (it starts a Task but does not await it).
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Dispatch took {sw.ElapsedMilliseconds} ms — it should return immediately.");
        // No message yet (handler is still sleeping).
        Assert.Empty(capture.Captured);
    }

    // -----------------------------------------------------------------------
    // OrgId and UserId parsed from string fields

    [Fact]
    public async Task OrgId_ParsedFromStringField()
    {
        var dict = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal);
        var decl = DeclarationFactory.FromToolType(typeof(EchoContextTool));
        dict[decl.Key] = decl;

        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(dict, capture.SendAsync);
        dispatcher.Dispatch(new ToolCallRequest
        {
            ToolKey        = "disp.echo_ctx",
            InvocationId   = "org-parse",
            ArgsJson       = ByteString.CopyFrom(Encoding.UTF8.GetBytes("{}")),
            OrganizationId = "12345",
            UserId         = "0",
        });

        await WaitForMessages(capture, count: 1);

        var resp = capture.Captured.Single().ToolCallResponse;
        Assert.Equal(ToolCallResponse.ResultOneofCase.ResultJson, resp.ResultCase);
        using var doc = JsonDocument.Parse(resp.ResultJson.ToStringUtf8());
        Assert.Equal(12345, doc.RootElement.GetProperty("OrgId").GetInt32());
    }

    // -----------------------------------------------------------------------
    // ERP identity fields populated on ToolContext

    [Fact]
    public async Task ErpIdentityFields_SurfaceOnContext_WhenPresentInRequest()
    {
        var dict = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal);
        var decl = DeclarationFactory.FromToolType(typeof(EchoContextTool));
        dict[decl.Key] = decl;

        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(dict, capture.SendAsync);
        var req = new ToolCallRequest
        {
            ToolKey        = "disp.echo_ctx",
            InvocationId   = "erp-1",
            ArgsJson       = ByteString.CopyFrom(Encoding.UTF8.GetBytes("{}")),
            OrganizationId = "1",
            UserId         = "0",
            EmployeeNo     = "EMP-001",
            ErpIdentifier  = "SAP-XYZ",
        };
        req.ErpDepartmentIdentifiers.Add("DEPT-A");
        req.ErpDepartmentIdentifiers.Add("DEPT-B");
        dispatcher.Dispatch(req);

        await WaitForMessages(capture, count: 1);

        var resp = capture.Captured.Single().ToolCallResponse;
        Assert.Equal(ToolCallResponse.ResultOneofCase.ResultJson, resp.ResultCase);
        using var doc = JsonDocument.Parse(resp.ResultJson.ToStringUtf8());
        var root = doc.RootElement;
        Assert.Equal("EMP-001", root.GetProperty("EmployeeNo").GetString());
        Assert.Equal("SAP-XYZ", root.GetProperty("ErpIdentifier").GetString());
        var depts = root.GetProperty("ErpDepartmentIdentifiers").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(new[] { "DEPT-A", "DEPT-B" }, depts);
    }

    [Fact]
    public async Task ErpIdentityFields_DefaultToEmptyWhenAbsentInRequest()
    {
        var dict = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal);
        var decl = DeclarationFactory.FromToolType(typeof(EchoContextTool));
        dict[decl.Key] = decl;

        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(dict, capture.SendAsync);
        dispatcher.Dispatch(new ToolCallRequest
        {
            ToolKey        = "disp.echo_ctx",
            InvocationId   = "erp-2",
            ArgsJson       = ByteString.CopyFrom(Encoding.UTF8.GetBytes("{}")),
            OrganizationId = "1",
            UserId         = "0",
            // EmployeeNo, ErpIdentifier, and ErpDepartmentIdentifiers intentionally omitted.
        });

        await WaitForMessages(capture, count: 1);

        var resp = capture.Captured.Single().ToolCallResponse;
        Assert.Equal(ToolCallResponse.ResultOneofCase.ResultJson, resp.ResultCase);
        using var doc = JsonDocument.Parse(resp.ResultJson.ToStringUtf8());
        var root = doc.RootElement;
        Assert.Equal("", root.GetProperty("EmployeeNo").GetString());
        Assert.Equal("", root.GetProperty("ErpIdentifier").GetString());
        Assert.Empty(root.GetProperty("ErpDepartmentIdentifiers").EnumerateArray());
    }

    // -----------------------------------------------------------------------
    // Helper

    private static async Task WaitForMessages(CapturingSend capture, int count, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (capture.Captured.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(capture.Captured.Count >= count,
            $"Expected {count} message(s) within {timeoutMs} ms but got {capture.Captured.Count}.");
    }
}

// ---------------------------------------------------------------------------
// Fixture that records and echoes the ToolContext
// ---------------------------------------------------------------------------

[Tool(Key = "disp.echo_ctx", Description = "Echo ToolContext as JSON")]
public class EchoContextTool : ToolHandler<EchoContextTool.Args, EchoContextTool.CtxResult>
{
    public class Args { }
    public class CtxResult
    {
        public int OrgId { get; set; }
        public string AgentKey { get; set; } = "";
        public string RunId { get; set; } = "";
        public string ConversationId { get; set; } = "";
        public string UserEmail { get; set; } = "";
        public int UserId { get; set; }
        public string EmployeeNo { get; set; } = "";
        public string ErpIdentifier { get; set; } = "";
        public string[] ErpDepartmentIdentifiers { get; set; } = Array.Empty<string>();
    }

    public override Task<CtxResult> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new CtxResult
        {
            OrgId                    = ctx.OrgId,
            AgentKey                 = ctx.AgentKey,
            RunId                    = ctx.RunId,
            ConversationId           = ctx.ConversationId,
            UserEmail                = ctx.UserEmail,
            UserId                   = ctx.UserId,
            EmployeeNo               = ctx.EmployeeNo,
            ErpIdentifier            = ctx.ErpIdentifier,
            ErpDepartmentIdentifiers = ctx.ErpDepartmentIdentifiers.ToArray(),
        });
}

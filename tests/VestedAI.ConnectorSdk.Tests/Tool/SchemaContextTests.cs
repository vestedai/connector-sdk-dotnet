using System.Text;
using System.Text.Json;
using Google.Protobuf;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Tests.Runtime;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Tool;

public class SchemaContextTests
{
    [Fact]
    public void CarriesTheTablesTheCoreResolved()
    {
        var ctx = new SchemaContext(
            new[] { new SchemaContextTable("Item Ledger Entry", "ASG", "table",
                new[] { "ASG$Item Ledger Entry$437dbf0e-84ff-417a-965d-ed2bb9650972" }) },
            HasStar: false, GateMode: "enforce");

        Assert.Equal("Item Ledger Entry", ctx.Tables[0].LogicalName);
        Assert.Single(ctx.Tables[0].Physical);
        Assert.Equal("enforce", ctx.GateMode);
    }

    [Fact]
    public void NullContextIsNotAnEmptyTableList_ViaTheRealDispatcher()
    {
        // Final whole-branch review, MINOR 6: the version of this test that
        // used to live here checked `Assert.NotNull(decided)` on a value it
        // had just constructed with `new SchemaContext(...)` two lines above
        // — a non-nullable record constructor can never return null, so that
        // half asserted nothing about the SDK at all. This is the file a
        // connector author reads as the worked example, so it now drives the
        // REAL mapping (Dispatcher.BuildContext -> ToSchemaContext) the way
        // DispatcherTests.cs's SchemaContext_* tests do, for the state
        // neither file covered yet: PRESENT but Tables EMPTY — the gate
        // decided and resolved nothing, which is the state the whole
        // absent-vs-empty guarantee is about.
        var toolContext = new ToolContext(OrgId: 1, AgentKey: "a", RunId: "r", ConversationId: "c");
        Assert.Null(toolContext.SchemaContext);

        var absent = DispatchAndCaptureSchemaContext(present: false);
        Assert.Equal(JsonValueKind.Null, absent.ValueKind);

        var present = DispatchAndCaptureSchemaContext(present: true);
        Assert.Equal(JsonValueKind.Object, present.ValueKind);
        Assert.Equal(0, present.GetProperty("Tables").GetArrayLength());
    }

    private static JsonElement DispatchAndCaptureSchemaContext(bool present)
    {
        var dict = new Dictionary<string, ToolDeclaration>(StringComparer.Ordinal);
        var decl = DeclarationFactory.FromToolType(typeof(EchoContextTool));
        dict[decl.Key] = decl;

        var capture = new CapturingSend();
        var dispatcher = new Dispatcher(dict, capture.SendAsync);
        var req = new Vested.V1.ToolCallRequest
        {
            ToolKey        = "disp.echo_ctx",
            InvocationId   = present ? "sc-empty-present" : "sc-still-absent",
            ArgsJson       = ByteString.CopyFrom(Encoding.UTF8.GetBytes("{}")),
            OrganizationId = "1",
            UserId         = "0",
        };
        if (present)
        {
            req.SchemaContext = new Vested.V1.SchemaContext { HasStar = false, GateMode = "enforce" };
            // Tables intentionally left empty — this IS the present-but-empty state.
        }

        dispatcher.Dispatch(req);

        var deadline = DateTime.UtcNow.AddMilliseconds(3000);
        while (capture.Captured.Count < 1 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.True(capture.Captured.Count >= 1, "expected one dispatched message");

        var resp = capture.Captured.Single().ToolCallResponse;
        using var doc = JsonDocument.Parse(resp.ResultJson.ToStringUtf8());

        return doc.RootElement.GetProperty("SchemaContext").Clone();
    }
}

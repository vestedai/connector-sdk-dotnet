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
    public void NullContextIsNotAnEmptyTableList()
    {
        // A handler treating null as "no tables touched" approves everything.
        var toolContext = new ToolContext(OrgId: 1, AgentKey: "a", RunId: "r", ConversationId: "c");
        Assert.Null(toolContext.SchemaContext);

        var decided = new SchemaContext(Array.Empty<SchemaContextTable>(), false, "enforce");
        Assert.NotNull(decided);
        Assert.Empty(decided.Tables);
    }
}

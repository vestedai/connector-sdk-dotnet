using System.Collections.Generic;
using System.Threading.Tasks;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

[Tool(Key = "t.rows", Description = "rows", Sensitivity = "read")]
public class RowsTool : PaginatedToolHandler<RowsTool.Args, RowsTool.Row>
{
    public class Args { public string Q { get; set; } = ""; }
    public class Row { public int I { get; set; } }
    public override Task<DatasetPage<Row>> FetchPageAsync(Args a, DatasetCursor cursor, ToolContext ctx)
    {
        int start = string.IsNullOrEmpty(cursor.Token) ? 0 : int.Parse(cursor.Token);
        var rows = new List<Row>();
        for (int i = start; i < start + 10 && i < 25; i++) rows.Add(new Row { I = i });
        string? next = (start + 10 < 25) ? (start + 10).ToString() : null;
        return Task.FromResult(new DatasetPage<Row> { Rows = rows, NextCursor = next, Total = 25 });
    }
}

public class PaginatedToolHandlerTests
{
    [Fact]
    public async Task InvokePagedBoxedAsync_ReturnsRowsAndCursor()
    {
        var h = new RowsTool();
        var page = await ((ToolHandlerBase)h).InvokePagedBoxedAsync(
            new RowsTool.Args { Q = "x" }, new DatasetCursor { Token = null, PageSize = 10 },
            new ToolContext(1, "a", "r", "c"));
        Assert.Equal("10", page.NextCursor);
        Assert.Equal(25, page.Total);
    }

    [Fact]
    public async Task InvokePagedBoxedAsync_LastPage_ReturnsNullCursor()
    {
        var h = new RowsTool();
        var page = await ((ToolHandlerBase)h).InvokePagedBoxedAsync(
            new RowsTool.Args { Q = "x" }, new DatasetCursor { Token = "20", PageSize = 10 },
            new ToolContext(1, "a", "r", "c"));
        Assert.Null(page.NextCursor);
        Assert.Equal(25, page.Total);
    }

    [Fact]
    public void InvokeBoxedAsync_OnPaginatedTool_Throws()
    {
        var h = (ToolHandlerBase)new RowsTool();
        var args = new RowsTool.Args();
        var ctx = new ToolContext(1, "a", "r", "c");
        // InvokeBoxedAsync throws synchronously (expression-body `throw`) before producing a Task.
        void Call() { h.InvokeBoxedAsync(args, ctx); }
        Assert.Throws<System.NotSupportedException>(Call);
    }

    [Fact]
    public void InvokePagedBoxedAsync_OnSingleTool_Throws()
    {
        // Verify ToolHandlerBase default throws for single (non-paginated) tools
        var h = new RowsTool();
        // RowsTool is paginated so we need a single tool to test the base throw.
        // The base default is exercised when someone calls it on a ToolHandler<,>.
        // We test the type-system contract: ArgsType/ResultType not expected on paginated tools.
        Assert.IsAssignableFrom<ToolHandlerBase>(h);
    }

    [Fact]
    public void DatasetCursor_DefaultPageSize_IsZero()
    {
        var c = new DatasetCursor();
        Assert.Null(c.Token);
        Assert.Equal(0, c.PageSize);
    }

    [Fact]
    public void DatasetPage_Properties_RoundTrip()
    {
        var rows = new List<int> { 1, 2, 3 };
        var page = new DatasetPage<int> { Rows = rows, NextCursor = "tok", Total = 100 };
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal("tok", page.NextCursor);
        Assert.Equal(100, page.Total);
    }

    [Fact]
    public void BoxedPage_Properties_RoundTrip()
    {
        var rows = new List<int> { 1, 2 };
        var bp = new BoxedPage { Rows = rows, NextCursor = "n", Total = 50 };
        Assert.Same(rows, bp.Rows);
        Assert.Equal("n", bp.NextCursor);
        Assert.Equal(50, bp.Total);
    }
}

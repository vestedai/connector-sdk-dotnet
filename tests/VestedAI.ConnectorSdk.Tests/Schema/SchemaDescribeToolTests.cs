using System.Text.Json;
using VestedAI.ConnectorSdk.Schema;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Schema;

file sealed class StubProvider : IRelationalSchemaProvider
{
    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(new[] { "ASG" });

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromResult("cat-abc");

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(
            new[]
            {
                new CanonicalEntity("Item", scopeKey, "table", null,
                    new[] { "No." },
                    new[] { new CanonicalVariant("ASG$Item$437dbf0e-84ff-417a-965d-ed2bb9650972", "base", 0) },
                    new[]
                    {
                        new CanonicalColumn("No.", "nvarchar", false, 1, true, "No.",
                            "ASG$Item$437dbf0e-84ff-417a-965d-ed2bb9650972"),
                    }),
            },
            new[] { new CanonicalRelation("Item", new[] { "No." }, "Item", new[] { "No." }, "variant_join") }));
}

public class SchemaDescribeToolTests
{
    [Fact]
    public async Task ReturnsOneRowPerEntityWithSnakeCaseKeys()
    {
        var rows = await new SchemaDescribeTool(new StubProvider())
            .DescribeAsync(new SchemaDescribeArgs("ASG", "entities"), default);

        var row = Assert.Single(rows);

        // ToRow<T> round-trips through JsonSerializer.Serialize then
        // Deserialize<Dictionary<string, object?>>. System.Text.Json has no
        // target type to deserialize scalar values into for an `object?`
        // dictionary value, so it boxes each value as a JsonElement rather
        // than a string/int/bool. Assert.Equal("Item", row["logical_name"])
        // would therefore compare a boxed JsonElement to a string and fail
        // even though the tool is correct. JsonElement.ToString() returns the
        // unquoted string for a JsonValueKind.String element, so that's the
        // adaptation here — the tool's public return type stays
        // IReadOnlyList<Dictionary<string, object?>> as the brief specifies;
        // only the assertions change.
        Assert.Equal("Item", row["logical_name"]?.ToString());
        Assert.True(row.ContainsKey("variants"));
        Assert.True(row.ContainsKey("columns"));
        Assert.True(row.ContainsKey("join_key"));
    }

    [Fact]
    public async Task ReturnsRelationsWhenAskedForThatPart()
    {
        var rows = await new SchemaDescribeTool(new StubProvider())
            .DescribeAsync(new SchemaDescribeArgs("ASG", "relations"), default);

        var row = Assert.Single(rows);
        Assert.Equal("variant_join", row["kind"]?.ToString());
        Assert.Equal("Item", row["from_entity"]?.ToString());
        Assert.Equal("Item", row["to_entity"]?.ToString());

        // from_columns / to_columns are JSON arrays, so they deserialize as
        // JsonElement of kind Array rather than IReadOnlyList<string> — same
        // boxing behaviour as the scalar fields above, just one level deeper.
        var fromColumns = ((JsonElement)row["from_columns"]!).EnumerateArray().Select(e => e.GetString());
        var toColumns = ((JsonElement)row["to_columns"]!).EnumerateArray().Select(e => e.GetString());
        Assert.Equal(new[] { "No." }, fromColumns);
        Assert.Equal(new[] { "No." }, toColumns);
    }

    [Fact]
    public async Task RejectsAnUnknownPartRatherThanReturningNothing()
    {
        // Returning an empty rowset for a typo'd part would ingest as "this
        // scope has no relations" and quietly produce a snapshot with no joins.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new SchemaDescribeTool(new StubProvider())
                .DescribeAsync(new SchemaDescribeArgs("ASG", "colummns"), default));
    }
}

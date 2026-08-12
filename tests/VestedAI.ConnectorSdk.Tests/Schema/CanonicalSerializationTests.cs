using System.Text.Json;
using VestedAI.ConnectorSdk.Schema;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Schema;

public class CanonicalSerializationTests
{
    [Fact]
    public void EmitsSnakeCaseKeysTheCoreCanRead()
    {
        // The core reads these rows with CanonicalEntity::fromArray(), which
        // keys on snake_case. The SDK otherwise serialises PascalCase — that
        // mismatch is exactly what made the production `Sql` argument key
        // unreadable, so it is pinned here rather than discovered later.
        var entity = new CanonicalEntity(
            LogicalName: "Item",
            ScopeKey: "ASG",
            Kind: "table",
            Comment: null,
            JoinKey: new[] { "No." },
            Variants: new[]
            {
                new CanonicalVariant("ASG$Item$437dbf0e-84ff-417a-965d-ed2bb9650972", "base", 0),
            },
            Columns: new[]
            {
                new CanonicalColumn("No.", "nvarchar", false, 1, true, "No.",
                    "ASG$Item$437dbf0e-84ff-417a-965d-ed2bb9650972"),
            });

        var json = JsonSerializer.Serialize(entity, CanonicalJson.Options);

        Assert.Contains("\"logical_name\":\"Item\"", json);
        Assert.Contains("\"scope_key\":\"ASG\"", json);
        Assert.Contains("\"join_key\":[\"No.\"]", json);
        Assert.Contains("\"physical_name\":", json);
        Assert.Contains("\"variant_physical_name\":", json);
        Assert.Contains("\"is_pk\":true", json);
    }

    [Fact]
    public void RelationSerialisesWithSnakeCaseEndpoints()
    {
        var rel = new CanonicalRelation("Item Ledger Entry", new[] { "Item No." },
            "Item", new[] { "No." }, "fk");

        var json = JsonSerializer.Serialize(rel, CanonicalJson.Options);

        Assert.Contains("\"from_entity\":\"Item Ledger Entry\"", json);
        Assert.Contains("\"to_columns\":[\"No.\"]", json);
    }

    [Fact]
    public void SchemaSerialisesNestedGraphInOneCall()
    {
        // CanonicalSchema is what IRelationalSchemaProvider.DescribeAsync
        // returns, but neither test above ever constructs or serialises one —
        // its own "entities"/"relations" keys, and the nested Variant/Column
        // fields, were only ever exercised as a side effect. This proves the
        // options propagate through the whole object graph in a single
        // Serialize call, which the other two tests rely on implicitly.
        var variant = new CanonicalVariant(
            "ASG$Customer$5b1e9b3a-2f34-4a11-9b0d-2a6a2e6a2c11", "extension", 2);
        var column = new CanonicalColumn(
            "Name", "nvarchar", true, 3, false, "Customer Name",
            "ASG$Customer$5b1e9b3a-2f34-4a11-9b0d-2a6a2e6a2c11");
        var entity = new CanonicalEntity(
            LogicalName: "Customer",
            ScopeKey: "ASG",
            Kind: "table",
            Comment: null,
            JoinKey: new[] { "No." },
            Variants: new[] { variant },
            Columns: new[] { column });
        var relation = new CanonicalRelation(
            "Sales Line", new[] { "No." }, "Customer", new[] { "No." }, "fk");
        var schema = new CanonicalSchema(new[] { entity }, new[] { relation });

        var json = JsonSerializer.Serialize(schema, CanonicalJson.Options);

        // Top-level container keys.
        Assert.Contains("\"entities\":[", json);
        Assert.Contains("\"relations\":[", json);

        // Nested entity/relation content is still snake_case inside the
        // container, not just at the top level.
        Assert.Contains("\"logical_name\":\"Customer\"", json);
        Assert.Contains("\"from_entity\":\"Sales Line\"", json);

        // CanonicalVariant fields, asserted directly for the first time.
        Assert.Contains("\"role\":\"extension\"", json);
        Assert.Contains("\"ordinal\":2", json);

        // CanonicalColumn fields, asserted directly for the first time.
        // variant_physical_name and is_pk are already covered elsewhere.
        Assert.Contains("\"name\":\"Name\"", json);
        Assert.Contains("\"type\":\"nvarchar\"", json);
        Assert.Contains("\"nullable\":true", json);
        Assert.Contains("\"position\":3", json);
        Assert.Contains("\"caption\":\"Customer Name\"", json);
    }
}

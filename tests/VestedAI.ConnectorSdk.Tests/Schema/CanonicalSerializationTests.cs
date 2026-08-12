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
}

using System.Text.Json;
using VestedAI.ConnectorSdk.Reflection;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// ---------------------------------------------------------------------------
// Regression: some NJsonSchema 11.x versions serialize the "any" schema for an
// object-valued dictionary (Dictionary<string, object>) as
//   "additionalProperties": []
// — an empty ARRAY, which is invalid against the JSON Schema metaschema
// (additionalProperties must be a boolean or a schema object). The hub's strict
// validator (santhosh-tekuri) then refuses to COMPILE the schema, failing every
// call with "output schema compile: ... not valid against metaschema". Observed
// in prod on erp_bc.data.run_sql (Rows: List<Dictionary<string,object>>).
//
// The SDK must not trust the transitive NJsonSchema version to emit valid
// JSON Schema — NormalizeSchemaDialect defensively repairs array-valued
// additionalProperties to {} (empty schema = allow any value).
// ---------------------------------------------------------------------------

public class SchemaDialectRepairTests
{
    // The exact invalid shape observed in prod (erp_bc.data.run_sql output schema).
    private const string ProdRunSqlSchema = """
    {
      "type": "object",
      "title": "Result",
      "$schema": "http://json-schema.org/draft-07/schema#",
      "properties": {
        "Rows": {
          "type": "array",
          "items": { "type": "object", "additionalProperties": [] },
          "description": "Result rows as column-name → value maps."
        },
        "Columns": { "type": "array", "items": { "type": "string" } },
        "RowCount": { "type": "integer", "format": "int32" }
      },
      "additionalProperties": false
    }
    """;

    [Fact]
    public void NormalizeSchemaDialect_RepairsArrayValuedAdditionalProperties()
    {
        var fixedJson = DeclarationFactory.NormalizeSchemaDialect(ProdRunSqlSchema);

        using var doc = JsonDocument.Parse(fixedJson);
        var ap = doc.RootElement
            .GetProperty("properties").GetProperty("Rows")
            .GetProperty("items").GetProperty("additionalProperties");

        Assert.Equal(JsonValueKind.Object, ap.ValueKind);  // {} (empty schema = allow any), not []
        Assert.Empty(ap.EnumerateObject());
    }

    [Fact]
    public void NormalizeSchemaDialect_LeavesValidAdditionalPropertiesUntouched()
    {
        // A legitimate boolean additionalProperties:false must survive unchanged.
        var fixedJson = DeclarationFactory.NormalizeSchemaDialect(ProdRunSqlSchema);

        using var doc = JsonDocument.Parse(fixedJson);
        var rootAp = doc.RootElement.GetProperty("additionalProperties");
        Assert.Equal(JsonValueKind.False, rootAp.ValueKind);
    }
}

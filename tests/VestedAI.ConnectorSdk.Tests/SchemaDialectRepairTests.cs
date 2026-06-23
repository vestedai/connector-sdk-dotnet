using System.Text.Json;
using VestedAI.ConnectorSdk.Reflection;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// ---------------------------------------------------------------------------
// Regression: the "any" schema for an object-valued dictionary
// (Dictionary<string, object>) is emitted as
//   "additionalProperties": {}      (NJsonSchema 11.6.x)
//   "additionalProperties": []      (some older NJsonSchema 11.x — already invalid)
// The empty-object form {} is valid JSON Schema on its own, but it is silently
// corrupted to the invalid array form [] by any hop that round-trips the schema
// through a PHP associative decode (json_decode($s, true) cannot tell {} from []) —
// e.g. the ConnectorHub's Laravel baseline store. The hub's strict validator
// (santhosh-tekuri) then refuses to COMPILE the schema, failing every call with
// "output schema compile: ... not valid against metaschema". Observed in prod on
// erp_bc.data.run_sql (Rows: List<Dictionary<string,object>>).
//
// NormalizeSchemaDialect normalizes BOTH the empty-object and the array form to
// the boolean "additionalProperties": true — semantically identical ("allow any"),
// and a scalar that survives the lossy round-trip intact.
// ---------------------------------------------------------------------------

public class SchemaDialectRepairTests
{
    // The invalid array shape observed on older NJsonSchema 11.x.
    private const string ProdRunSqlSchemaArray = """
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

    // The valid-but-fragile empty-object shape emitted by NJsonSchema 11.6.x —
    // the one that actually reaches the hub and is corrupted downstream to [].
    private const string ProdRunSqlSchemaEmptyObject = """
    {
      "type": "object",
      "title": "Result",
      "$schema": "http://json-schema.org/draft-07/schema#",
      "properties": {
        "Rows": {
          "type": "array",
          "items": { "type": "object", "additionalProperties": {} },
          "description": "Result rows as column-name → value maps."
        },
        "Columns": { "type": "array", "items": { "type": "string" } },
        "RowCount": { "type": "integer", "format": "int32" }
      },
      "additionalProperties": false
    }
    """;

    [Theory]
    [InlineData(true)]   // array form  -> true
    [InlineData(false)]  // empty-object form -> true
    public void NormalizeSchemaDialect_NormalizesAllowAnyToBooleanTrue(bool arrayForm)
    {
        var input = arrayForm ? ProdRunSqlSchemaArray : ProdRunSqlSchemaEmptyObject;

        var fixedJson = DeclarationFactory.NormalizeSchemaDialect(input);

        using var doc = JsonDocument.Parse(fixedJson);
        var ap = doc.RootElement
            .GetProperty("properties").GetProperty("Rows")
            .GetProperty("items").GetProperty("additionalProperties");

        // "allow any" must become the scalar `true` — the form that survives a
        // PHP associative round-trip (json_decode(..., true)) without corruption.
        Assert.Equal(JsonValueKind.True, ap.ValueKind);
    }

    [Fact]
    public void NormalizeSchemaDialect_LeavesAdditionalPropertiesFalseUntouched()
    {
        // A legitimate boolean additionalProperties:false ("no additional
        // properties") must survive unchanged — it is NOT an "allow any" schema.
        var fixedJson = DeclarationFactory.NormalizeSchemaDialect(ProdRunSqlSchemaEmptyObject);

        using var doc = JsonDocument.Parse(fixedJson);
        var rootAp = doc.RootElement.GetProperty("additionalProperties");
        Assert.Equal(JsonValueKind.False, rootAp.ValueKind);
    }
}

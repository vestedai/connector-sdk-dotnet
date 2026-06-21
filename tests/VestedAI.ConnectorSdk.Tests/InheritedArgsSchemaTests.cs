using System.ComponentModel;
using System.Text.Json;
using NJsonSchema;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// ---------------------------------------------------------------------------
// Regression: a tool whose Args type uses class inheritance must produce a
// SATISFIABLE input schema. NJsonSchema's default representation of inheritance
// is `allOf: [{$ref: base}, {derived props, additionalProperties:false}]` with
// the base definition ALSO additionalProperties:false. That composition is
// unsatisfiable: additionalProperties:false is evaluated per-subschema, so the
// base-level fields are rejected by the derived branch and vice-versa — only
// `{}` validates. Real connectors (erp_bc.retail.sum_sales) hit this and every
// tool call fails with "additional properties ... not allowed".
//
// The fix flattens the inheritance hierarchy into a single object schema.
// ---------------------------------------------------------------------------

public class SumBaseArgs
{
    [Description("Optional preset for a single sum request.")]
    public string Preset { get; set; } = "";

    [Description("Optional filters applied before summing.")]
    public List<string> Filters { get; set; } = new();
}

public class RetailSumArgs : SumBaseArgs
{
    [Description("Optional. Partial store name to resolve to Store No.")]
    public string StoreNameContains { get; set; } = "";
}

public class SumResult
{
    public decimal Total { get; set; }
}

[Tool(Key = "t.sum_inherited", Description = "sum with inherited args", Sensitivity = "read")]
public class InheritedArgsTool : ToolHandler<RetailSumArgs, SumResult>
{
    public override Task<SumResult> HandleAsync(RetailSumArgs a, ToolContext c)
        => Task.FromResult(new SumResult { Total = 0 });
}

public class InheritedArgsSchemaTests
{
    [Fact]
    public void InheritedArgs_InputSchema_IsFlattened_NoRootAllOf_WithAllProperties()
    {
        var decl = DeclarationFactory.FromToolType(typeof(InheritedArgsTool));
        using var doc = JsonDocument.Parse(decl.InputSchemaJson);
        var root = doc.RootElement;

        // Broken NJsonSchema inheritance emits a root `allOf` (unsatisfiable with
        // additionalProperties:false). A flattened schema must not.
        Assert.False(root.TryGetProperty("allOf", out _),
            "input schema must flatten inheritance, not use allOf");

        // Both the inherited (base) and derived properties must live at the root.
        Assert.True(root.TryGetProperty("properties", out var props),
            "flattened schema must expose properties at the root");
        Assert.True(props.TryGetProperty("Preset", out _),
            "inherited base property 'Preset' missing from root properties");
        Assert.True(props.TryGetProperty("Filters", out _),
            "inherited base property 'Filters' missing from root properties");
        Assert.True(props.TryGetProperty("StoreNameContains", out _),
            "derived property 'StoreNameContains' missing from root properties");
    }

    [Fact]
    public async Task InheritedArgs_InputSchema_AcceptsBaseAndDerivedPayload()
    {
        var decl = DeclarationFactory.FromToolType(typeof(InheritedArgsTool));
        var schema = await JsonSchema.FromJsonAsync(decl.InputSchemaJson);

        // A real call mixes base (Preset) and derived (StoreNameContains) fields.
        var errors = schema.Validate(
            "{\"Preset\":\"posSales\",\"StoreNameContains\":\"Exit 9\"}");

        Assert.Empty(errors);
    }
}

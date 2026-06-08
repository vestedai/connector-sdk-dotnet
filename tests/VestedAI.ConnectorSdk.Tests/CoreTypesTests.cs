using System.ComponentModel;
using System.Text;
using System.Text.Json;
using VestedAI.ConnectorSdk;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

[Tool(Key = "t.echo", Description = "echo", Sensitivity = "destructive")]
public class EchoTool : ToolHandler<EchoTool.Args, EchoTool.Result>
{
    public class Args
    {
        [Description("Echo me.")]
        public string Text { get; set; } = "";
    }

    public class Result
    {
        public string Echoed { get; set; } = "";
    }

    public override Task<Result> HandleAsync(Args a, ToolContext c)
        => Task.FromResult(new Result { Echoed = a.Text });
}

[Tool(Key = "t.bogus", Description = "bogus", Sensitivity = "bogus")]
public class BogusToolHandler : ToolHandler<BogusToolHandler.Args, BogusToolHandler.Result>
{
    public class Args { public string X { get; set; } = ""; }
    public class Result { public string Y { get; set; } = ""; }
    public override Task<Result> HandleAsync(Args a, ToolContext c)
        => Task.FromResult(new Result { Y = a.X });
}

[Tool(Key = "t.nosens", Description = "no sensitivity")]
public class NoSensitivityTool : ToolHandler<NoSensitivityTool.Args, NoSensitivityTool.Result>
{
    public class Args { public int Value { get; set; } }
    public class Result { public int Doubled { get; set; } }
    public override Task<Result> HandleAsync(Args a, ToolContext c)
        => Task.FromResult(new Result { Doubled = a.Value * 2 });
}

[Agent(Key = "a.test", Name = "TestAgent", Model = "openai:gpt-4o", Description = "Test agent")]
[Instruction(Type = "system", Position = 0, Body = "You are a test agent.")]
[Instruction(Type = "user", Position = 1, Body = "Be helpful.")]
public class TestAgent { }

[Agent(Key = "a.reversed", Name = "Reversed", Model = "openai:gpt-4o")]
[Instruction(Type = "system", Position = 10, Body = "Tenth instruction.")]
[Instruction(Type = "system", Position = 1, Body = "First instruction.")]
[Instruction(Type = "system", Position = 5, Body = "Fifth instruction.")]
public class ReversedInstructionsAgent { }

// Plain class with no [Agent] attribute — used for negative test.
public class NotAnAgent { }

// A class with [Tool] but NOT derived from ToolHandler<,> — used for negative test.
[Tool(Key = "t.nothandler", Description = "no handler")]
public class NotAHandler { }

// ---------------------------------------------------------------------------
// Tests — ToolDeclaration from [Tool]
// ---------------------------------------------------------------------------

public class FromToolTypeTests
{
    [Fact]
    public void EchoTool_Key_IsCorrect()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        Assert.Equal("t.echo", decl.Key);
    }

    [Fact]
    public void EchoTool_Sensitivity_IsDestructive()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        Assert.Equal("destructive", decl.Sensitivity);
    }

    [Fact]
    public void EchoTool_Name_DefaultsToKey_WhenAttributeNameIsEmpty()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        // EchoTool does not set Name on the attribute → falls back to Key.
        Assert.Equal("t.echo", decl.Name);
    }

    [Fact]
    public void EchoTool_InputSchema_ContainsDescriptionAnnotation()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        // NJsonSchema should pick up [Description("Echo me.")] on the Text property.
        Assert.Contains("Echo me.", decl.InputSchemaJson);
    }

    [Fact]
    public void EchoTool_InputSchema_IsValidJson()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        // Must parse without throwing.
        using var doc = JsonDocument.Parse(decl.InputSchemaJson);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Schemas_UseDraft07Dialect_NotDraft04()
    {
        // The hub validates declared schemas with opis/json-schema, which does
        // NOT support draft-04 (NJsonSchema's default). The SDK must emit
        // draft-07 or the hub rejects registration with schema_invalid.
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        foreach (var schema in new[] { decl.InputSchemaJson, decl.OutputSchemaJson! })
        {
            using var doc = JsonDocument.Parse(schema);
            var dialect = doc.RootElement.GetProperty("$schema").GetString();
            Assert.Equal("http://json-schema.org/draft-07/schema#", dialect);
            Assert.DoesNotContain("draft-04", schema);
        }
    }

    [Fact]
    public void EchoTool_OutputSchema_IsValidJson()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        Assert.NotNull(decl.OutputSchemaJson);
        using var doc = JsonDocument.Parse(decl.OutputSchemaJson!);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void EchoTool_ArgsType_IsArgs()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        Assert.Equal(typeof(EchoTool.Args), decl.ArgsType);
    }

    [Fact]
    public void EchoTool_ResultType_IsResult()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        Assert.Equal(typeof(EchoTool.Result), decl.ResultType);
    }

    [Fact]
    public void EchoTool_DefaultDeadlineMs_Is30000()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        Assert.Equal(30_000, decl.DefaultDeadlineMs);
    }

    [Fact]
    public void EchoTool_MaxResultBytes_Is1MiB()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        Assert.Equal(1_048_576, decl.MaxResultBytes);
    }

    [Fact]
    public void BogusToolHandler_InvalidSensitivity_ThrowsConnectorException()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => DeclarationFactory.FromToolType(typeof(BogusToolHandler)));
        Assert.Contains("bogus", ex.Message);
        // Should mention allowed values.
        Assert.Contains("read", ex.Message);
        Assert.Contains("destructive", ex.Message);
    }

    [Fact]
    public void NoSensitivityTool_Sensitivity_IsEmpty()
    {
        var decl = DeclarationFactory.FromToolType(typeof(NoSensitivityTool));
        Assert.Equal("", decl.Sensitivity);
    }

    [Fact]
    public void NotAHandler_MissingToolHandlerBase_ThrowsConnectorException()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => DeclarationFactory.FromToolType(typeof(NotAHandler)));
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void NotAnAgent_MissingToolAttribute_ThrowsConnectorException()
    {
        // NotAnAgent has no [Tool] attribute → should throw.
        var ex = Assert.Throws<ConnectorException>(
            () => DeclarationFactory.FromToolType(typeof(NotAnAgent)));
        Assert.Contains("[Tool]", ex.Message);
    }
}

// ---------------------------------------------------------------------------
// Tests — AgentDeclaration from [Agent] + [Instruction]
// ---------------------------------------------------------------------------

public class FromAgentTypeTests
{
    [Fact]
    public void TestAgent_Key_IsCorrect()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(TestAgent));
        Assert.Equal("a.test", decl.Key);
    }

    [Fact]
    public void TestAgent_Name_IsCorrect()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(TestAgent));
        Assert.Equal("TestAgent", decl.Name);
    }

    [Fact]
    public void TestAgent_Model_IsCorrect()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(TestAgent));
        Assert.Equal("openai:gpt-4o", decl.Model);
    }

    [Fact]
    public void TestAgent_Description_IsCorrect()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(TestAgent));
        Assert.Equal("Test agent", decl.Description);
    }

    [Fact]
    public void TestAgent_Status_DefaultsToActive()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(TestAgent));
        Assert.Equal("active", decl.Status);
    }

    [Fact]
    public void TestAgent_HasTwoInstructions()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(TestAgent));
        Assert.Equal(2, decl.Instructions.Count);
    }

    [Fact]
    public void TestAgent_InstructionsSortedByPosition()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(TestAgent));
        Assert.Equal(0, decl.Instructions[0].Position);
        Assert.Equal(1, decl.Instructions[1].Position);
    }

    [Fact]
    public void ReversedInstructionsAgent_InstructionsSortedByPosition()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(ReversedInstructionsAgent));
        // Defined in order 10, 1, 5 → sorted 1, 5, 10.
        Assert.Equal(3, decl.Instructions.Count);
        Assert.Equal(1, decl.Instructions[0].Position);
        Assert.Equal(5, decl.Instructions[1].Position);
        Assert.Equal(10, decl.Instructions[2].Position);
    }

    [Fact]
    public void ReversedInstructionsAgent_InstructionBodies_AreCorrect()
    {
        var decl = DeclarationFactory.FromAgentType(typeof(ReversedInstructionsAgent));
        Assert.Equal("First instruction.", decl.Instructions[0].Body);
        Assert.Equal("Fifth instruction.", decl.Instructions[1].Body);
        Assert.Equal("Tenth instruction.", decl.Instructions[2].Body);
    }

    [Fact]
    public void NotAnAgent_MissingAgentAttribute_ThrowsConnectorException()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => DeclarationFactory.FromAgentType(typeof(NotAnAgent)));
        Assert.Contains("[Agent]", ex.Message);
    }
}

// ---------------------------------------------------------------------------
// Tests — ArgsValidation.Parse
// ---------------------------------------------------------------------------

public class ArgsValidationTests
{
    private static ToolDeclaration GetEchoDecl()
        => DeclarationFactory.FromToolType(typeof(EchoTool));

    [Fact]
    public void Parse_ValidJson_RoundTrips()
    {
        var decl = GetEchoDecl();
        var json = Encoding.UTF8.GetBytes("{\"text\":\"hi\"}");
        var result = ArgsValidation.Parse(decl, json);
        var args = Assert.IsType<EchoTool.Args>(result);
        Assert.Equal("hi", args.Text);
    }

    [Fact]
    public void Parse_CaseInsensitiveKey_Works()
    {
        var decl = GetEchoDecl();
        var json = Encoding.UTF8.GetBytes("{\"TEXT\":\"hello\"}");
        var result = ArgsValidation.Parse(decl, json);
        var args = Assert.IsType<EchoTool.Args>(result);
        Assert.Equal("hello", args.Text);
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsToolValidationException()
    {
        var decl = GetEchoDecl();
        var json = Encoding.UTF8.GetBytes("{not valid json}");
        var ex = Assert.Throws<ToolValidationException>(
            () => ArgsValidation.Parse(decl, json));
        Assert.Equal("t.echo", ex.ToolKey);
    }

    [Fact]
    public void Parse_NullJson_ThrowsToolValidationException()
    {
        var decl = GetEchoDecl();
        // "null" in JSON deserializes to null for a reference type.
        var json = Encoding.UTF8.GetBytes("null");
        var ex = Assert.Throws<ToolValidationException>(
            () => ArgsValidation.Parse(decl, json));
        Assert.Equal("t.echo", ex.ToolKey);
    }

    [Fact]
    public void Parse_EmptyObject_UsesDefaults()
    {
        var decl = GetEchoDecl();
        var json = Encoding.UTF8.GetBytes("{}");
        var result = ArgsValidation.Parse(decl, json);
        var args = Assert.IsType<EchoTool.Args>(result);
        Assert.Equal("", args.Text);  // default value
    }
}

// ---------------------------------------------------------------------------
// Tests — ToolContext record shape
// ---------------------------------------------------------------------------

public class ToolContextTests
{
    [Fact]
    public void ToolContext_AllParams_Settable()
    {
        var ctx = new ToolContext(
            OrgId:          42,
            AgentKey:       "a.test",
            RunId:          "run-1",
            ConversationId: "conv-1",
            UserEmail:      "user@example.com",
            UserId:         7);

        Assert.Equal(42, ctx.OrgId);
        Assert.Equal("a.test", ctx.AgentKey);
        Assert.Equal("run-1", ctx.RunId);
        Assert.Equal("conv-1", ctx.ConversationId);
        Assert.Equal("user@example.com", ctx.UserEmail);
        Assert.Equal(7, ctx.UserId);
    }

    [Fact]
    public void ToolContext_Defaults_UserEmailEmpty_UserIdZero()
    {
        var ctx = new ToolContext(1, "a.key", "r", "c");
        Assert.Equal("", ctx.UserEmail);
        Assert.Equal(0, ctx.UserId);
    }
}

// ---------------------------------------------------------------------------
// Tests — Sensitivity constants
// ---------------------------------------------------------------------------

public class SensitivityTests
{
    [Fact]
    public void All_Contains_FiveValues()
    {
        Assert.Equal(5, Sensitivity.All.Length);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("destructive")]
    [InlineData("external_call")]
    [InlineData("medium")]
    public void All_ContainsExpectedValues(string value)
    {
        Assert.Contains(value, Sensitivity.All);
    }
}

// ---------------------------------------------------------------------------
// Tests — Error types
// ---------------------------------------------------------------------------

public class ErrorTypeTests
{
    [Fact]
    public void TokenException_IsConnectorException()
    {
        var ex = new TokenException("token rejected");
        Assert.IsAssignableFrom<ConnectorException>(ex);
    }

    [Fact]
    public void ToolValidationException_IsConnectorException_WithToolKey()
    {
        var ex = new ToolValidationException("my.tool", "bad args");
        Assert.IsAssignableFrom<ConnectorException>(ex);
        Assert.Equal("my.tool", ex.ToolKey);
        Assert.Equal("bad args", ex.Message);
    }
}

// ---------------------------------------------------------------------------
// Tests — ToolDeclaration.InvokeAsync round-trip
// ---------------------------------------------------------------------------

public class ToolDeclarationInvokeTests
{
    [Fact]
    public async Task InvokeAsync_EchoTool_ReturnsCorrectResult()
    {
        var decl = DeclarationFactory.FromToolType(typeof(EchoTool));
        var ctx = new ToolContext(1, "a.test", "r1", "c1");
        var args = new EchoTool.Args { Text = "hello" };

        var result = await decl.InvokeAsync(args, ctx);
        var echoed = Assert.IsType<EchoTool.Result>(result);
        Assert.Equal("hello", echoed.Echoed);
    }
}

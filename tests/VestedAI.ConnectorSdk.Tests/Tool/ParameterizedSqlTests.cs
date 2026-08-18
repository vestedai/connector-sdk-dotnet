using System.Text;
using System.Text.Json;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Tool;

public class ParameterizedSqlTests
{
    [Fact]
    public void ScalarsArePassedThroughToTheDriverUnchanged()
    {
        var input = new Dictionary<string, object?>
        {
            ["from"] = "2026-01-01",
            ["count"] = 42,
            ["active"] = true,
            ["id"] = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        };

        var result = ParameterizedSql.Normalise(input);

        Assert.Equal("2026-01-01", result["from"]);
        Assert.Equal(42, result["count"]);
        Assert.Equal(true, result["active"]);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result["id"]);
    }

    [Fact]
    public void NullValuePassesThroughAsNull()
    {
        var input = new Dictionary<string, object?> { ["maybe"] = null };

        var result = ParameterizedSql.Normalise(input);

        Assert.True(result.ContainsKey("maybe"));
        Assert.Null(result["maybe"]);
    }

    [Fact]
    public void NullParametersMapReturnsAnEmptyMap()
    {
        var result = ParameterizedSql.Normalise(null);

        Assert.Empty(result);
    }

    [Fact]
    public void ArraysBecomeOneJsonStringParameter()
    {
        var input = new Dictionary<string, object?> { ["locs"] = new[] { "A", "B" } };

        var result = ParameterizedSql.Normalise(input);

        // Exactly one bind parameter came out — the array was not expanded
        // into "locs0", "locs1", ... placeholders.
        Assert.Single(result);
        var value = Assert.IsType<string>(result["locs"]);
        Assert.Equal("[\"A\",\"B\"]", value);
    }

    [Fact]
    public void ToJsonArraySerialisesValuesAsOneJsonArrayString()
    {
        var json = ParameterizedSql.ToJsonArray(new object?[] { "A", "B", 3 });

        Assert.Equal("[\"A\",\"B\",3]", json);
    }

    // The property the whole design rests on. Normalise moves VALUES and must
    // never sanitise, escape, or reinterpret one — the driver's bind does
    // that.
    [Fact]
    public void AValueContainingSqlSurvivesVerbatim()
    {
        const string malicious = "2026-01-01'; DROP TABLE x --";
        var input = new Dictionary<string, object?> { ["from"] = malicious };

        var result = ParameterizedSql.Normalise(input);

        Assert.Equal(malicious, result["from"]);
        // Not merely equal — the exact same string instance came back,
        // proving nothing re-copied, re-escaped, or reinterpreted it.
        Assert.Same(malicious, result["from"]);
    }

    [Fact]
    public void AValueThatCannotBeBoundIsRefused()
    {
        var input = new Dictionary<string, object?>
        {
            ["filter"] = new Dictionary<string, object?> { ["nested"] = "x" },
        };

        var ex = Assert.Throws<ArgumentException>(() => ParameterizedSql.Normalise(input));

        Assert.Equal("filter", ex.ParamName);
        Assert.Contains("filter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlainObjectValueIsAlsoRefused()
    {
        var input = new Dictionary<string, object?> { ["thing"] = new UnbindableWidget() };

        var ex = Assert.Throws<ArgumentException>(() => ParameterizedSql.Normalise(input));

        Assert.Equal("thing", ex.ParamName);
    }

    private sealed class UnbindableWidget
    {
        public string Name { get; set; } = "widget";
    }

    // -----------------------------------------------------------------
    // Through the REAL deserialization path.
    //
    // A hand-built Dictionary<string, object?> of concrete CLR types (above)
    // is not the shape a tool handler actually receives. ArgsValidation.Parse
    // deserializes incoming args JSON with plain JsonSerializer and no custom
    // converter, so a POCO property typed Dictionary<string, object?> comes
    // back with every value boxed as a JsonElement — never a bare string,
    // long, or bool. These tests go through that exact path so a regression
    // in JsonElement handling fails here, not only in production traffic.
    // -----------------------------------------------------------------

    private sealed class QueryArgs
    {
        public string Sql { get; set; } = "";
        public Dictionary<string, object?>? Params { get; set; }
    }

    private static ToolDeclaration QueryToolDecl() => new()
    {
        Key = "erp.query_sql", Name = "erp.query_sql", Description = "d", Sensitivity = "read",
        DefaultDeadlineMs = 30_000, MaxResultBytes = 1_048_576,
        InputSchemaJson = "{}", HandlerType = typeof(object),
        ArgsType = typeof(QueryArgs), ResultType = typeof(object),
    };

    private static QueryArgs ParseQueryArgs(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var parsed = ArgsValidation.Parse(QueryToolDecl(), bytes);
        return Assert.IsType<QueryArgs>(parsed);
    }

    [Fact]
    public void RealDeserialization_JsonElementScalarsNormaliseToTheirClrValue()
    {
        var args = ParseQueryArgs("""
            {"sql":"SELECT 1","params":{"from":"2026-01-01","count":3,"active":true,"note":null}}
            """);

        // Every value on args.Params is a boxed JsonElement at this point —
        // not a string/long/bool — which is exactly the shape Normalise must
        // handle.
        Assert.IsType<JsonElement>(args.Params!["from"]);

        var result = ParameterizedSql.Normalise(args.Params);

        var from = Assert.IsType<string>(result["from"]);
        Assert.Equal("2026-01-01", from);

        var count = Assert.IsType<long>(result["count"]);
        Assert.Equal(3L, count);

        var active = Assert.IsType<bool>(result["active"]);
        Assert.True(active);

        Assert.True(result.ContainsKey("note"));
        Assert.Null(result["note"]);
    }

    [Fact]
    public void RealDeserialization_JsonElementNumberFallsBackToDoubleWhenItDoesNotFitALong()
    {
        var args = ParseQueryArgs("""{"sql":"SELECT 1","params":{"rate":3.5}}""");

        var result = ParameterizedSql.Normalise(args.Params);

        var rate = Assert.IsType<double>(result["rate"]);
        Assert.Equal(3.5, rate);
    }

    [Fact]
    public void RealDeserialization_JsonElementArrayBecomesOneJsonStringParameter()
    {
        var args = ParseQueryArgs("""{"sql":"SELECT 1","params":{"locs":["A","B"]}}""");

        var result = ParameterizedSql.Normalise(args.Params);

        Assert.Single(result);
        var locs = Assert.IsType<string>(result["locs"]);
        Assert.Equal("[\"A\",\"B\"]", locs);
    }

    [Fact]
    public void RealDeserialization_JsonElementObjectIsRefused()
    {
        var args = ParseQueryArgs("""{"sql":"SELECT 1","params":{"filter":{"nested":"x"}}}""");

        var ex = Assert.Throws<ArgumentException>(() => ParameterizedSql.Normalise(args.Params));

        Assert.Equal("filter", ex.ParamName);
    }

    // The end-to-end version of AValueContainingSqlSurvivesVerbatim above:
    // proven through ArgsValidation.Parse's real JsonElement deserialization,
    // not only against a hand-built dictionary.
    [Fact]
    public void RealDeserialization_AValueContainingSqlSurvivesVerbatim()
    {
        const string malicious = "2026-01-01'; DROP TABLE x --";
        var json = JsonSerializer.Serialize(new
        {
            sql = "SELECT 1",
            @params = new Dictionary<string, object?> { ["from"] = malicious },
        });

        var args = ParseQueryArgs(json);
        var result = ParameterizedSql.Normalise(args.Params);

        Assert.Equal(malicious, result["from"]);
    }
}

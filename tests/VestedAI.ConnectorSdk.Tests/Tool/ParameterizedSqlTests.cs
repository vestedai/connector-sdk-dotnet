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
}

using VestedAI.ConnectorSdk;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Reflection;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Schema;
using VestedAI.ConnectorSdk.Tests.Runtime;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Schema;

// ---------------------------------------------------------------------------
// The agent and the two tools a relational source points at. Build() now
// cross-checks the declared tool keys and the SQL argument against these, so
// the fixtures below name real tools — a connector's declaration that names
// nothing real is itself a test case, further down.
// ---------------------------------------------------------------------------

[Agent(Key = "rs.demo", Name = "RelationalDemoAgent", Model = "openai:gpt-4o")]
public class RelationalDemoAgent { }

[Tool(Key = "rs.demo.describe_schema", Description = "Return the canonical schema.", Sensitivity = "read")]
public class RelationalDescribeTool : ToolHandler<RelationalDescribeTool.Args, RelationalDescribeTool.Result>
{
    public class Args { public string Scope { get; set; } = ""; public string Part { get; set; } = ""; }
    public class Result { public int RowCount { get; set; } }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { RowCount = 0 });
}

[Tool(Key = "rs.demo.query_sql", Description = "Run SQL.", Sensitivity = "read")]
public class RelationalQueryTool : ToolHandler<RelationalQueryTool.Args, RelationalQueryTool.Result>
{
    // "Sql" is the wire name: this SDK generates input schemas with the CLR
    // property names, so the declaration's SqlArg must be "Sql", not "sql".
    public class Args { public string Sql { get; set; } = ""; public string Scope { get; set; } = ""; }
    public class Result { public int RowCount { get; set; } }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { RowCount = 0 });
}

/// <summary>
/// A query tool whose wire argument name differs from its CLR property name.
/// Exists so a test can tell which of the two the host validates against.
/// </summary>
[Tool(Key = "rs.demo.query_renamed", Description = "Run SQL, renamed arg.", Sensitivity = "read")]
public class RelationalRenamedQueryTool
    : ToolHandler<RelationalRenamedQueryTool.Args, RelationalRenamedQueryTool.Result>
{
    public class Args
    {
        [System.Text.Json.Serialization.JsonPropertyName("sql_text")]
        public string Sql { get; set; } = "";
    }

    public class Result { public int RowCount { get; set; } }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { RowCount = 0 });
}

// ---------------------------------------------------------------------------
// Fixtures — providers decorated with [RelationalSource].
//
// Each returns a distinguishing scope name from ScopesAsync so a test can prove
// which instance the host actually built, rather than merely that something
// non-null came back.
// ---------------------------------------------------------------------------

/// <summary>Default-constructible provider — the SDK builds this one itself.</summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class FixtureRelationalProvider : FixtureProviderBase
{
    /// <summary>Marker returned by <see cref="ScopesAsync"/>.</summary>
    public const string Marker = "activated-by-sdk";

    public override Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(new[] { Marker });
}

/// <summary>
/// The realistic case: a provider with a constructor dependency (a connection
/// factory / catalog reader), exactly like <see cref="SqlServerProvider"/>.
/// It can only reach the host through <c>UseRelationalSchemaProvider</c>.
/// </summary>
[RelationalSource(
    Engine = "mysql",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class FixtureDependencyProvider : FixtureProviderBase
{
    private readonly string _scope;

    public FixtureDependencyProvider(string scope) => _scope = scope;

    public override Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(new[] { _scope });
}

/// <summary>Second annotated provider — for the one-per-assembly rule.</summary>
[RelationalSource(
    Engine = "mysql",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class FixtureSecondRelationalProvider : FixtureProviderBase { }

/// <summary>
/// The documented one-liner from <see cref="RelationalSourceAttribute"/>'s
/// example: annotate a SUBCLASS of the SDK's own provider. If
/// <see cref="SqlServerProvider"/> were sealed again, or the attribute were
/// required somewhere else, this stops compiling — which is the point of having
/// the documented shape exist as a compiled fixture.
/// </summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class FixtureSubclassedProvider(ICatalogReader reader) : SqlServerProvider(reader);

/// <summary>Decorated but does not implement the provider interface.</summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class FixtureNotAProvider { }

// --- Declarations that name something that does not exist -------------------

[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schemaa",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class FixtureUnknownDescribeTool : FixtureProviderBase { }

[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sqll",
    SqlArg = "Sql")]
public sealed class FixtureUnknownQueryTool : FixtureProviderBase { }

/// <summary>
/// The casing trap: a lowercase arg name on an SDK whose input schemas carry
/// the CLR property names. Register would be accepted and the gate would read
/// null forever.
/// </summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "sql")]
public sealed class FixtureWrongCaseSqlArg : FixtureProviderBase { }

/// <summary>Declares the WIRE argument name — must be accepted.</summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_renamed",
    SqlArg = "sql_text")]
public sealed class FixtureWireNameSqlArg : FixtureProviderBase { }

/// <summary>Declares the CLR property name — must be refused.</summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_renamed",
    SqlArg = "Sql")]
public sealed class FixtureClrNameSqlArg : FixtureProviderBase { }

// --- Blank-field fixtures, one per required value ---------------------------

[RelationalSource(DescribeTool = "rs.demo.describe_schema", QueryTool = "rs.demo.query_sql", SqlArg = "Sql")]
public sealed class FixtureMissingEngine : FixtureProviderBase { }

[RelationalSource(Engine = "sqlserver", QueryTool = "rs.demo.query_sql", SqlArg = "Sql")]
public sealed class FixtureMissingDescribeTool : FixtureProviderBase { }

[RelationalSource(Engine = "sqlserver", DescribeTool = "rs.demo.describe_schema", SqlArg = "Sql")]
public sealed class FixtureMissingQueryTool : FixtureProviderBase { }

[RelationalSource(Engine = "sqlserver", DescribeTool = "rs.demo.describe_schema", QueryTool = "rs.demo.query_sql")]
public sealed class FixtureMissingSqlArg : FixtureProviderBase { }

// --- Task 9 (L3e): scopes/default_scope bootstrap forcing function ----------

/// <summary>Several scopes, no default — must throw.</summary>
[RelationalSource(
    Engine = "mysql", DescribeTool = "rs.demo.describe_schema", QueryTool = "rs.demo.query_sql", SqlArg = "Sql",
    Scopes = new[] { "production", "erp_middleware_production" })]
public sealed class FixtureScopesNoDefault : FixtureProviderBase { }

/// <summary>A default_scope naming something that is not one of Scopes — must throw.</summary>
[RelationalSource(
    Engine = "mysql", DescribeTool = "rs.demo.describe_schema", QueryTool = "rs.demo.query_sql", SqlArg = "Sql",
    Scopes = new[] { "production" }, DefaultScope = "erp_middleware")]
public sealed class FixtureDefaultScopeNotDeclared : FixtureProviderBase { }

/// <summary>One scope, no default — must be accepted.</summary>
[RelationalSource(
    Engine = "sqlserver", DescribeTool = "rs.demo.describe_schema", QueryTool = "rs.demo.query_sql", SqlArg = "Sql",
    Scopes = new[] { "ASG" })]
public sealed class FixtureSingleScopeNoDefault : FixtureProviderBase { }

/// <summary>No scopes at all — backward compatibility: today's connectors declare neither field.</summary>
[RelationalSource(Engine = "mysql", DescribeTool = "rs.demo.describe_schema", QueryTool = "rs.demo.query_sql", SqlArg = "Sql")]
public sealed class FixtureNoScopesDeclared : FixtureProviderBase { }

/// <summary>Several scopes with a valid default — must be accepted, and both must reach the wire.</summary>
[RelationalSource(
    Engine = "mysql", DescribeTool = "rs.demo.describe_schema", QueryTool = "rs.demo.query_sql", SqlArg = "Sql",
    Scopes = new[] { "production", "erp_middleware_production" }, DefaultScope = "production")]
public sealed class FixtureScopesWithValidDefault : FixtureProviderBase { }

/// <summary>Shared no-op body for the fixtures above.</summary>
public abstract class FixtureProviderBase : IRelationalSchemaProvider
{
    public virtual Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromResult("fixture-fingerprint");
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class RelationalSourceDeclarationTests
{
    private static readonly FakeAssembly _declaringAssembly = new FakeAssembly(
        typeof(RelationalDemoAgent),
        typeof(RelationalDescribeTool),
        typeof(RelationalQueryTool),
        typeof(FixtureRelationalProvider));

    private static readonly FakeAssembly _silentAssembly = new FakeAssembly(
        typeof(RelationalDemoAgent),
        typeof(RelationalDescribeTool),
        typeof(RelationalQueryTool));

    /// <summary>The demo agent and its two tools, plus whatever else is needed.</summary>
    private static FakeAssembly AssemblyWith(params Type[] extra) => new FakeAssembly(
        new[] { typeof(RelationalDemoAgent), typeof(RelationalDescribeTool), typeof(RelationalQueryTool) }
            .Concat(extra).ToArray());

    // -----------------------------------------------------------------------
    // The declaration reaches the app with the exact declared values.

    [Fact]
    public void Build_ScannedProvider_CarriesEveryDeclaredValue()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_declaringAssembly)
            .Build();

        Assert.NotNull(app.RelationalSource);
        Assert.Equal("sqlserver", app.RelationalSource!.Engine);
        Assert.Equal("rs.demo.describe_schema", app.RelationalSource.DescribeTool);
        Assert.Equal("rs.demo.query_sql", app.RelationalSource.QueryTool);
        Assert.Equal("Sql", app.RelationalSource.SqlArg);
        Assert.Equal(typeof(FixtureRelationalProvider), app.RelationalSource.ProviderType);
    }

    [Fact]
    public async Task Build_ScannedProvider_IsAUsableInstance()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_declaringAssembly)
            .Build();

        Assert.NotNull(app.RelationalSchemaProvider);

        // Not just non-null: call it, and assert the value only THIS provider
        // returns. Task 3 awaits CatalogFingerprintAsync on this instance, so
        // "a live object that answers" is the property that matters.
        var scopes = await app.RelationalSchemaProvider!.ScopesAsync(CancellationToken.None);
        Assert.Equal(new[] { FixtureRelationalProvider.Marker }, scopes);
    }

    [Fact]
    public void Build_NoAnnotatedProvider_LeavesBothNull()
    {
        // "Declare nothing, stay untouched": a connector that fronts no
        // relational database must register no relational_source, which is what
        // keeps it invisible to schema extraction.
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_silentAssembly)
            .Build();

        Assert.Null(app.RelationalSource);
        Assert.Null(app.RelationalSchemaProvider);
    }

    // -----------------------------------------------------------------------
    // The explicit-instance override — the reason the attribute alone is not
    // enough. A provider that takes a connection factory (SqlServerProvider and
    // every real one) cannot be default-constructed.

    [Fact]
    public async Task UseRelationalSchemaProvider_SuppliedInstance_IsUsedAndDeclares()
    {
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_silentAssembly)
            .UseRelationalSchemaProvider(new FixtureDependencyProvider("company-a"))
            .Build();

        Assert.NotNull(app.RelationalSource);
        Assert.Equal("mysql", app.RelationalSource!.Engine);
        Assert.Equal("rs.demo.describe_schema", app.RelationalSource.DescribeTool);
        Assert.Equal("rs.demo.query_sql", app.RelationalSource.QueryTool);
        Assert.Equal("Sql", app.RelationalSource.SqlArg);
        Assert.Equal(typeof(FixtureDependencyProvider), app.RelationalSource.ProviderType);

        var scopes = await app.RelationalSchemaProvider!.ScopesAsync(CancellationToken.None);
        Assert.Equal(new[] { "company-a" }, scopes);
    }

    [Fact]
    public void UseRelationalSchemaProvider_OverridesTheScannedType()
    {
        // Same declared type, but the constructed instance must be the one the
        // caller supplied — otherwise a provider holding a live connection is
        // silently replaced by a freshly Activator-built one.
        var supplied = new FixtureRelationalProvider();

        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_declaringAssembly)
            .UseRelationalSchemaProvider(supplied)
            .Build();

        Assert.Same(supplied, app.RelationalSchemaProvider);
    }

    [Fact]
    public async Task UseRelationalSchemaProvider_SubclassOfSqlServerProvider_IsAccepted()
    {
        // The exact shape the attribute's <example> documents. It is only
        // possible because SqlServerProvider is not sealed; a connector cannot
        // annotate the SDK's class, but it can annotate a one-line subclass.
        var supplied = new FixtureSubclassedProvider(new FakeCatalog());

        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(_silentAssembly)
            .UseRelationalSchemaProvider(supplied)
            .Build();

        Assert.Same(supplied, app.RelationalSchemaProvider);
        Assert.Equal("sqlserver", app.RelationalSource!.Engine);

        // Inherited behaviour still works through the subclass.
        var scopes = await app.RelationalSchemaProvider!.ScopesAsync(CancellationToken.None);
        Assert.Empty(scopes);
    }

    [Fact]
    public void UseRelationalSchemaProvider_UnannotatedSdkProvider_NamesTheRemedy()
    {
        // The documented failure: handing over a bare SqlServerProvider. The
        // message must send the author to their OWN assembly, not to a type
        // they did not write.
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(_silentAssembly)
                .UseRelationalSchemaProvider(new SqlServerProvider(new FakeCatalog())));

        Assert.Contains(nameof(SqlServerProvider), ex.Message);
        Assert.Contains("your own assembly", ex.Message);
        Assert.Contains("Subclass", ex.Message);
    }

    [Fact]
    public void Build_ProviderWithoutParameterlessCtor_AndNoInstance_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(AssemblyWith(typeof(FixtureDependencyProvider)))
                .Build());

        Assert.Contains(nameof(FixtureDependencyProvider), ex.Message);
        Assert.Contains("UseRelationalSchemaProvider", ex.Message);
    }

    [Fact]
    public void UseRelationalSchemaProvider_ConflictsWithScannedType_SaysScanned()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(_declaringAssembly)
                .UseRelationalSchemaProvider(new FixtureDependencyProvider("company-a")));

        Assert.Contains(nameof(FixtureRelationalProvider), ex.Message);
        Assert.Contains(nameof(FixtureDependencyProvider), ex.Message);
        Assert.Contains("already scanned", ex.Message);
    }

    [Fact]
    public void UseRelationalSchemaProvider_ConflictsWithSuppliedType_SaysSupplied()
    {
        // Provenance matters: telling the operator the other one was "scanned"
        // sends them to grep an assembly that is perfectly fine.
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(_silentAssembly)
                .UseRelationalSchemaProvider(new FixtureDependencyProvider("company-a"))
                .UseRelationalSchemaProvider(new FixtureSubclassedProvider(new FakeCatalog())));

        Assert.Contains("already supplied to UseRelationalSchemaProvider", ex.Message);
        Assert.DoesNotContain("already scanned", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Build() cross-checks the declaration against the tools that exist.
    //
    // Nothing downstream does: the core validates non-emptiness and a namespace
    // prefix only, so a typo'd key or a mis-cased arg name registers CLEANLY and
    // the gate then governs a tool nothing answers to while the real query tool
    // runs ungoverned.

    [Fact]
    public void Build_DescribeToolNotDeclared_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(AssemblyWith(typeof(FixtureUnknownDescribeTool)))
                .Build());

        Assert.Contains("DescribeTool", ex.Message);
        Assert.Contains("rs.demo.describe_schemaa", ex.Message);
        // The message lists what IS declared, so the typo is visible side by side.
        Assert.Contains("rs.demo.describe_schema,", ex.Message);
    }

    [Fact]
    public void Build_QueryToolNotDeclared_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(AssemblyWith(typeof(FixtureUnknownQueryTool)))
                .Build());

        Assert.Contains("QueryTool", ex.Message);
        Assert.Contains("rs.demo.query_sqll", ex.Message);
        // The trailing ")" matters: "rs.demo.query_sqll" contains the real key
        // as a prefix, so asserting the bare key would pass on the typo alone.
        Assert.Contains("rs.demo.query_sql)", ex.Message);
    }

    [Fact]
    public void Build_SqlArgIsNotAnArgumentOfTheQueryTool_Throws()
    {
        // Lowercase "sql" against an input schema that carries "Sql".
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(AssemblyWith(typeof(FixtureWrongCaseSqlArg)))
                .Build());

        Assert.Contains("SqlArg 'sql'", ex.Message);
        Assert.Contains("rs.demo.query_sql", ex.Message);
        Assert.Contains("including case", ex.Message);

        // Names the arguments the tool really takes, so the casing fix is
        // obvious. Asserted on the list itself — "Sql" also occurs inside
        // "SqlArg", so a bare Contains would pass without any list at all.
        Assert.Contains("arguments are: ", ex.Message);
        var listed = ex.Message[(ex.Message.IndexOf("arguments are: ", StringComparison.Ordinal)
                                 + "arguments are: ".Length)..];
        Assert.Contains("Sql", listed);
        Assert.Contains("Scope", listed);
    }

    [Fact]
    public void Build_SqlArg_ResolvesAgainstTheWireSchema_NotClrPropertyNames()
    {
        // The gate reads the argument the CALLER sent, whose name comes from the
        // input schema — not from the CLR property. Where the two diverge, a
        // reflection-based check would accept a declaration that reads null at
        // gate time and authorizes an empty string. This pins which one is used.
        var decl = DeclarationFactory.FromToolType(typeof(RelationalRenamedQueryTool));
        using var doc = System.Text.Json.JsonDocument.Parse(decl.InputSchemaJson);
        var props = doc.RootElement.GetProperty("properties");

        // Precondition — without a real divergence this test proves nothing.
        Assert.True(props.TryGetProperty("sql_text", out _),
            "expected the wire name 'sql_text' in the generated schema");
        Assert.False(props.TryGetProperty("Sql", out _),
            "expected the CLR name 'Sql' NOT to appear in the generated schema");

        // The wire name is accepted.
        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(AssemblyWith(typeof(RelationalRenamedQueryTool), typeof(FixtureWireNameSqlArg)))
            .Build();
        Assert.Equal("sql_text", app.RelationalSource!.SqlArg);

        // The CLR name is refused — this is the case a reflection-based check
        // would have waved through.
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(AssemblyWith(typeof(RelationalRenamedQueryTool), typeof(FixtureClrNameSqlArg)))
                .Build());

        Assert.Contains("SqlArg 'Sql'", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Scanner — the fourth tuple element, and its singleton rule.

    [Fact]
    public void ScanAssembly_ReturnsTheRelationalSourceDeclaration()
    {
        var (_, _, _, relational) = Scanner.ScanAssembly(_declaringAssembly);

        Assert.NotNull(relational);
        Assert.Equal("sqlserver", relational!.Engine);
        Assert.Equal("rs.demo.describe_schema", relational.DescribeTool);
        Assert.Equal("rs.demo.query_sql", relational.QueryTool);
        Assert.Equal("Sql", relational.SqlArg);
        Assert.Equal(typeof(FixtureRelationalProvider), relational.ProviderType);
    }

    [Fact]
    public void ScanAssembly_NoAnnotatedProvider_ReturnsNull()
    {
        var (_, _, _, relational) = Scanner.ScanAssembly(_silentAssembly);
        Assert.Null(relational);
    }

    [Fact]
    public void ScanAssembly_TwoRelationalSources_ThrowsConnectorException()
    {
        // One connector fronts one relational source: two would leave the
        // platform guessing which query tool the SQL gate governs.
        var asm = new FakeAssembly(
            typeof(FixtureRelationalProvider),
            typeof(FixtureSecondRelationalProvider));

        var ex = Assert.Throws<ConnectorException>(() => Scanner.ScanAssembly(asm));

        Assert.Contains(nameof(FixtureRelationalProvider), ex.Message);
        Assert.Contains(nameof(FixtureSecondRelationalProvider), ex.Message);
    }

    [Fact]
    public void ScanAssembly_SameProviderTypeTwice_IsNotADuplicate()
    {
        // A type re-listed by an assembly proxy is harmless — same declaration.
        var asm = new FakeAssembly(
            typeof(FixtureRelationalProvider),
            typeof(FixtureRelationalProvider));

        var (_, _, _, relational) = Scanner.ScanAssembly(asm);
        Assert.Equal(typeof(FixtureRelationalProvider), relational!.ProviderType);
    }

    [Fact]
    public void ScanAssembly_TwoRelationalSourcesAcrossAssemblies_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(new FakeAssembly(typeof(FixtureRelationalProvider)))
                .ScanAssembly(new FakeAssembly(typeof(FixtureSecondRelationalProvider))));

        Assert.Contains(nameof(FixtureRelationalProvider), ex.Message);
        Assert.Contains(nameof(FixtureSecondRelationalProvider), ex.Message);
    }

    // -----------------------------------------------------------------------
    // DeclarationFactory — startup rejection of a mis-declared source.
    // Each message must NAME the field, or an operator reading a crash log
    // learns only that "something" was blank.

    [Theory]
    [InlineData(typeof(FixtureMissingEngine), "Engine")]
    [InlineData(typeof(FixtureMissingDescribeTool), "DescribeTool")]
    [InlineData(typeof(FixtureMissingQueryTool), "QueryTool")]
    [InlineData(typeof(FixtureMissingSqlArg), "SqlArg")]
    public void FromRelationalSourceType_BlankField_ThrowsNamingThatField(Type fixture, string fieldName)
    {
        var ex = Assert.Throws<ConnectorException>(
            () => DeclarationFactory.FromRelationalSourceType(fixture));

        Assert.Contains(fieldName, ex.Message);
        Assert.Contains(fixture.FullName!, ex.Message);
    }

    [Fact]
    public void FromRelationalSourceType_NotAProvider_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => DeclarationFactory.FromRelationalSourceType(typeof(FixtureNotAProvider)));

        Assert.Contains(nameof(IRelationalSchemaProvider), ex.Message);
    }

    [Fact]
    public void FromRelationalSourceType_MissingAttribute_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => DeclarationFactory.FromRelationalSourceType(typeof(SqlServerProvider)));

        Assert.Contains("[RelationalSource]", ex.Message);
    }

    [Fact]
    public void FromRelationalSourceType_CarriesNoFingerprint()
    {
        // A fingerprint captured at scan time is stale the moment the source
        // catalog changes; Task 3 reads it live from the provider instead. This
        // asserts the declaration has no such property to go stale.
        var propertyNames = typeof(RelationalSourceDeclaration)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain("Fingerprint", propertyNames);
    }

    // -----------------------------------------------------------------------
    // Task 9 (L3e): the forcing function. FromRelationalSourceType (called
    // from both ScanAssembly and UseRelationalSchemaProvider, i.e. always
    // before the worker dials the hub) throws when a relational source
    // declares more than one scope without a DefaultScope, or names a
    // DefaultScope that is not one of the declared scopes.
    //
    // Same seam and same reasoning as ConnectorHostBuilder.Build()'s
    // credential-key check ("Fail at startup rather than at the first
    // credential op: without a key every check would fail later with a
    // puzzling message.") — this failure belongs on the connector author's
    // deploy, not on a model's tool call at 00:30 in production.

    [Fact]
    public void FromRelationalSourceType_SeveralScopesNoDefault_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => DeclarationFactory.FromRelationalSourceType(typeof(FixtureScopesNoDefault)));

        Assert.Contains("default_scope", ex.Message);
    }

    [Fact]
    public void FromRelationalSourceType_DefaultScopeNotAmongScopes_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => DeclarationFactory.FromRelationalSourceType(typeof(FixtureDefaultScopeNotDeclared)));

        Assert.Contains("default_scope", ex.Message);
    }

    [Fact]
    public void FromRelationalSourceType_OneScopeNoDefault_IsAccepted()
    {
        var declared = DeclarationFactory.FromRelationalSourceType(typeof(FixtureSingleScopeNoDefault));

        Assert.Equal(new[] { "ASG" }, declared.Scopes);
        Assert.Equal("", declared.DefaultScope);
    }

    [Fact]
    public void FromRelationalSourceType_NoScopesAtAll_IsAccepted()
    {
        // Backward compatibility: today's connectors declare neither field.
        var declared = DeclarationFactory.FromRelationalSourceType(typeof(FixtureNoScopesDeclared));

        Assert.Empty(declared.Scopes);
        Assert.Equal("", declared.DefaultScope);
    }

    [Fact]
    public void FromRelationalSourceType_ScopesWithValidDefault_CarriesBothThrough()
    {
        var declared = DeclarationFactory.FromRelationalSourceType(typeof(FixtureScopesWithValidDefault));

        Assert.Equal(new[] { "production", "erp_middleware_production" }, declared.Scopes);
        Assert.Equal("production", declared.DefaultScope);
    }
}

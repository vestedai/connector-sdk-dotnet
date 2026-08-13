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
// Fixtures — providers decorated with [RelationalSource].
//
// Each returns a distinguishing scope name from ScopesAsync so a test can prove
// which instance the host actually built, rather than merely that something
// non-null came back.
// ---------------------------------------------------------------------------

/// <summary>Default-constructible provider — the SDK builds this one itself.</summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "erp_bc.describe_schema",
    QueryTool = "erp_bc.query_sql",
    SqlArg = "Sql")]
public sealed class FixtureRelationalProvider : IRelationalSchemaProvider
{
    /// <summary>Marker returned by <see cref="ScopesAsync"/>.</summary>
    public const string Marker = "activated-by-sdk";

    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(new[] { Marker });

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromResult("fixture-fingerprint");
}

/// <summary>
/// The realistic case: a provider with a constructor dependency (a connection
/// factory / catalog reader), exactly like <see cref="SqlServerProvider"/>.
/// It can only reach the host through <c>UseRelationalSchemaProvider</c>.
/// </summary>
[RelationalSource(
    Engine = "mysql",
    DescribeTool = "shop.describe_schema",
    QueryTool = "shop.query_sql",
    SqlArg = "sql")]
public sealed class FixtureDependencyProvider : IRelationalSchemaProvider
{
    private readonly string _scope;

    public FixtureDependencyProvider(string scope) => _scope = scope;

    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(new[] { _scope });

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromResult("dependency-fingerprint");
}

/// <summary>Second annotated provider — for the one-per-assembly rule.</summary>
[RelationalSource(
    Engine = "mysql",
    DescribeTool = "other.describe_schema",
    QueryTool = "other.query_sql",
    SqlArg = "sql")]
public sealed class FixtureSecondRelationalProvider : IRelationalSchemaProvider
{
    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromResult("second-fingerprint");
}

/// <summary>Decorated but does not implement the provider interface.</summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "bad.describe_schema",
    QueryTool = "bad.query_sql",
    SqlArg = "Sql")]
public sealed class FixtureNotAProvider { }

/// <summary>Every field blank — one per missing-field test, below.</summary>
[RelationalSource(DescribeTool = "b.describe", QueryTool = "b.query", SqlArg = "Sql")]
public sealed class FixtureMissingEngine : FixtureProviderBase { }

[RelationalSource(Engine = "sqlserver", QueryTool = "b.query", SqlArg = "Sql")]
public sealed class FixtureMissingDescribeTool : FixtureProviderBase { }

[RelationalSource(Engine = "sqlserver", DescribeTool = "b.describe", SqlArg = "Sql")]
public sealed class FixtureMissingQueryTool : FixtureProviderBase { }

[RelationalSource(Engine = "sqlserver", DescribeTool = "b.describe", QueryTool = "b.query")]
public sealed class FixtureMissingSqlArg : FixtureProviderBase { }

/// <summary>Shared no-op body for the rejection fixtures.</summary>
public abstract class FixtureProviderBase : IRelationalSchemaProvider
{
    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromResult("");
}

// ---------------------------------------------------------------------------
// A valid agent+tool pair, so Build()'s tool-prefix validation is satisfied and
// these tests fail for relational-source reasons only.
// ---------------------------------------------------------------------------

[Agent(Key = "rs.demo", Name = "RelationalDemoAgent", Model = "openai:gpt-4o")]
public class RelationalDemoAgent { }

[Tool(Key = "rs.demo.ping", Description = "Ping tool for relational-source tests.", Sensitivity = "read")]
public class RelationalDemoPingTool : ToolHandler<RelationalDemoPingTool.Args, RelationalDemoPingTool.Result>
{
    public class Args { public string Message { get; set; } = ""; }
    public class Result { public string Reply { get; set; } = ""; }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
        => Task.FromResult(new Result { Reply = args.Message });
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class RelationalSourceDeclarationTests
{
    private static readonly FakeAssembly _declaringAssembly = new FakeAssembly(
        typeof(RelationalDemoAgent),
        typeof(RelationalDemoPingTool),
        typeof(FixtureRelationalProvider));

    private static readonly FakeAssembly _silentAssembly = new FakeAssembly(
        typeof(RelationalDemoAgent),
        typeof(RelationalDemoPingTool));

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
        Assert.Equal("erp_bc.describe_schema", app.RelationalSource.DescribeTool);
        Assert.Equal("erp_bc.query_sql", app.RelationalSource.QueryTool);
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
        Assert.Equal("shop.describe_schema", app.RelationalSource.DescribeTool);
        Assert.Equal("shop.query_sql", app.RelationalSource.QueryTool);
        Assert.Equal("sql", app.RelationalSource.SqlArg);
        Assert.Equal(typeof(FixtureDependencyProvider), app.RelationalSource.ProviderType);

        var scopes = await app.RelationalSchemaProvider!.ScopesAsync(CancellationToken.None);
        Assert.Equal(new[] { "company-a" }, scopes);
    }

    [Fact]
    public async Task UseRelationalSchemaProvider_OverridesTheScannedType()
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
        await Task.CompletedTask;
    }

    [Fact]
    public void Build_ProviderWithoutParameterlessCtor_AndNoInstance_Throws()
    {
        var asm = new FakeAssembly(
            typeof(RelationalDemoAgent),
            typeof(RelationalDemoPingTool),
            typeof(FixtureDependencyProvider));

        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder().ScanAssembly(asm).Build());

        Assert.Contains(nameof(FixtureDependencyProvider), ex.Message);
        Assert.Contains("UseRelationalSchemaProvider", ex.Message);
    }

    [Fact]
    public void UseRelationalSchemaProvider_ConflictsWithScannedType_Throws()
    {
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(_declaringAssembly)
                .UseRelationalSchemaProvider(new FixtureDependencyProvider("company-a")));

        Assert.Contains(nameof(FixtureRelationalProvider), ex.Message);
        Assert.Contains(nameof(FixtureDependencyProvider), ex.Message);
    }

    // -----------------------------------------------------------------------
    // Scanner — the fourth tuple element, and its singleton rule.

    [Fact]
    public void ScanAssembly_ReturnsTheRelationalSourceDeclaration()
    {
        var (_, _, _, relational) = Scanner.ScanAssembly(_declaringAssembly);

        Assert.NotNull(relational);
        Assert.Equal("sqlserver", relational!.Engine);
        Assert.Equal("erp_bc.describe_schema", relational.DescribeTool);
        Assert.Equal("erp_bc.query_sql", relational.QueryTool);
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
        // One connector fronts one relational source: two declarations would
        // leave the platform guessing which query tool the SQL gate governs.
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
}

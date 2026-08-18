using Google.Protobuf;
using Vested.V1;
using VestedAI.ConnectorSdk;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Schema;
using VestedAI.ConnectorSdk.Tests.Runtime;
using VestedAI.ConnectorSdk.Tests.Schema;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// ---------------------------------------------------------------------------
// Proves RelationalSourceDecl exists in the .NET-vendored proto (Grpc.Tools
// generates it from Proto/connector_hub.proto at build time — there is no
// checked-in generated code) and that a Register carrying one round-trips
// through wire-format protobuf with every field intact.
//
// Field numbers are wire-format contract with the canonical proto
// (proto/vested/v1/connector_hub.proto): credential_schema = 3,
// relational_source = 4. This test only exercises correctness of a same-schema
// round trip; it does NOT by itself prove the numbering matches the canonical
// proto — see RelationalSourceDeclFieldNumberDrillTests for that.
// ---------------------------------------------------------------------------
public class RelationalSourceDeclTests
{
    [Fact]
    public void Register_WithRelationalSource_RoundTripsEveryField()
    {
        var original = new Register
        {
            BaselineFingerprint = "fp-abc123",
            RelationalSource = new RelationalSourceDecl
            {
                Engine = "sqlserver",
                DescribeTool = "erp.describe_schema",
                QueryTool = "erp.query_sql",
                SqlArg = "Sql",
                Fingerprint = "catalog-hash-9f8e7d",
            },
        };

        var bytes = original.ToByteArray();
        var parsed = Register.Parser.ParseFrom(bytes);

        Assert.NotNull(parsed.RelationalSource);
        Assert.Equal("sqlserver", parsed.RelationalSource.Engine);
        Assert.Equal("erp.describe_schema", parsed.RelationalSource.DescribeTool);
        Assert.Equal("erp.query_sql", parsed.RelationalSource.QueryTool);
        Assert.Equal("Sql", parsed.RelationalSource.SqlArg);
        Assert.Equal("catalog-hash-9f8e7d", parsed.RelationalSource.Fingerprint);
    }

    [Fact]
    public void Register_WithoutRelationalSource_StaysNullAfterRoundTrip()
    {
        // Absent = "this connector fronts no relational database" per the proto
        // comment — the same "declare nothing, stay untouched" contract as
        // credential_schema. Google.Protobuf's C# generator leaves an unset
        // singular message field null (never an auto-vivified empty instance),
        // so a caller can branch on presence with `is null` exactly as existing
        // code already does for CredentialSchema.
        var original = new Register { BaselineFingerprint = "fp-none" };

        var parsed = Register.Parser.ParseFrom(original.ToByteArray());

        Assert.Null(parsed.RelationalSource);
    }

    [Fact]
    public void RelationalSourceDecl_StandsAloneAndRoundTrips()
    {
        // Also exercise the message type on its own, independent of Register,
        // since Task 2/3 will construct it directly (e.g. from a connector's
        // declared decl) before ever touching a Register.
        var original = new RelationalSourceDecl
        {
            Engine = "mysql",
            DescribeTool = "d.describe",
            QueryTool = "d.query",
            SqlArg = "sql",
            Fingerprint = "f-1",
        };

        var parsed = RelationalSourceDecl.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(original.Engine, parsed.Engine);
        Assert.Equal(original.DescribeTool, parsed.DescribeTool);
        Assert.Equal(original.QueryTool, parsed.QueryTool);
        Assert.Equal(original.SqlArg, parsed.SqlArg);
        Assert.Equal(original.Fingerprint, parsed.Fingerprint);
    }

    // -----------------------------------------------------------------------
    // Task 5: ParamsArg (params_arg = 8, landed on the proto by Task 1).
    //
    // The two-hop .NET path — [RelationalSource] attribute →
    // RelationalSourceDeclaration → Daemon.ToProtoAsync's RelationalSourceDecl
    // — is the one place a declared value can be dropped silently: it compiles
    // and the record carries the value, but nothing puts it on the wire. So
    // these three go through Daemon.ToProtoAsync itself, not the bare proto
    // class, unlike the round-trip tests above.
    //
    // Fixture provider for rs.demo.query_sql (declared in
    // Schema/RelationalSourceDeclarationTests.cs, arguments "Sql" and "Scope").

    private sealed class ParamsArgFixtureProvider : IRelationalSchemaProvider
    {
        public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
            => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

        public Task<string> CatalogFingerprintAsync(CancellationToken ct)
            => Task.FromResult("");
    }

    [Fact]
    public async Task ToProtoAsync_ParamsArgDeclared_ReachesTheWireDeclaration()
    {
        var decl = await Daemon.ToProtoAsync(
            new RelationalSourceDeclaration(
                "mysql", "rs.demo.describe_schema", "rs.demo.query_sql", "Sql",
                Array.Empty<string>(), "", typeof(ParamsArgFixtureProvider),
                ParamsArg: "Params"),
            new ParamsArgFixtureProvider(),
            CancellationToken.None);

        Assert.Equal("Params", decl.ParamsArg);
    }

    [Fact]
    public async Task ToProtoAsync_ParamsArgOmitted_EmitsEmptyString()
    {
        // Legal, unlike an omitted SqlArg: a source that takes no bind
        // parameters must still boot and still register.
        var decl = await Daemon.ToProtoAsync(
            new RelationalSourceDeclaration(
                "mysql", "rs.demo.describe_schema", "rs.demo.query_sql", "Sql",
                Array.Empty<string>(), "", typeof(ParamsArgFixtureProvider)),
            new ParamsArgFixtureProvider(),
            CancellationToken.None);

        Assert.Equal("", decl.ParamsArg);
    }

    [RelationalSource(
        Engine = "sqlserver",
        DescribeTool = "rs.demo.describe_schema",
        QueryTool = "rs.demo.query_sql",
        SqlArg = "Sql",
        ParamsArg = "Bogus")]
    private sealed class FixtureUnknownParamsArg : FixtureProviderBase { }

    [Fact]
    public void Build_ParamsArgIsNotAnArgumentOfTheQueryTool_Throws()
    {
        // ParamsArg fails differently from SqlArg but just as quietly: name it
        // wrong and the connector never receives the parameters, so a filter
        // silently does not apply and a dashboard shows unfiltered numbers
        // that look plausible. Same fix as SqlArg — refuse at bootstrap.
        var ex = Assert.Throws<ConnectorException>(
            () => ConnectorHost.CreateBuilder()
                .ScanAssembly(new FakeAssembly(
                    typeof(RelationalDemoAgent),
                    typeof(RelationalDescribeTool),
                    typeof(RelationalQueryTool),
                    typeof(FixtureUnknownParamsArg)))
                .Build());

        Assert.Contains("ParamsArg 'Bogus'", ex.Message);
        Assert.Contains("rs.demo.query_sql", ex.Message);
        Assert.Contains("including case", ex.Message);

        Assert.Contains("arguments are: ", ex.Message);
        var listed = ex.Message[(ex.Message.IndexOf("arguments are: ", StringComparison.Ordinal)
                                 + "arguments are: ".Length)..];
        Assert.Contains("Sql", listed);
        Assert.Contains("Scope", listed);
    }
}

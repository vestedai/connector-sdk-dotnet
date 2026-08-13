using Google.Protobuf;
using Vested.V1;
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
}

using System.Text.Json;
using Vested.V1;
using VestedAI.ConnectorSdk.Credential;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Schema;
using VestedAI.ConnectorSdk.Tests.Fixtures;
using VestedAI.ConnectorSdk.Tests.Schema;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

// ---------------------------------------------------------------------------
// Fixtures — providers and a credential handler for the Register wire tests.
//
// They point at rs.demo.describe_schema / rs.demo.query_sql (declared in
// Schema/RelationalSourceDeclarationTests.cs) because Build() cross-checks the
// declared tool keys and the SQL argument against the tools that really exist.
// ---------------------------------------------------------------------------

/// <summary>
/// Answers with a fingerprint no other fixture returns, so a test can tell
/// "the value the provider produced" apart from "some non-empty string".
/// </summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class RegisterLiveFingerprintProvider : IRelationalSchemaProvider
{
    /// <summary>The value only this provider returns.</summary>
    public const string CatalogFingerprint = "catalog-hash-read-live-at-register";

    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromResult(CatalogFingerprint);
}

/// <summary>
/// The normal transient case: the source database is not reachable when the
/// connector starts. Returns a FAULTED task rather than throwing synchronously,
/// which is what a real async database call produces — and what a try/catch
/// that does not wrap the <c>await</c> would fail to catch.
/// </summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class RegisterUnreachableCatalogProvider : IRelationalSchemaProvider
{
    /// <summary>Message the warning must name.</summary>
    public const string Failure = "catalog host db-erp-01.internal refused: connection reset";

    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromException<string>(new InvalidOperationException(Failure));
}

/// <summary>
/// Minimal credential handler so one connector can declare BOTH a credential
/// schema and a relational source — the coexistence case.
/// </summary>
[Credential(Kind = "basic", Title = "Demo ERP account", HelpText = "Your ERP login.")]
[CredentialField(Key = "username", Label = "Username", Type = "text")]
[CredentialField(Key = "password", Label = "Password", Type = "password")]
public sealed class RegisterDemoCredentialHandler : IUserCredentialHandler
{
    public Task<CredentialValidation> ValidateAsync(
        CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.FromResult(CredentialValidation.Succeeded());

    public Task RevokeAsync(CredentialContext ctx, IReadOnlyDictionary<string, string> credential)
        => Task.CompletedTask;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

/// <summary>
/// Proves <c>relational_source</c> is put ON THE WIRE.
///
/// Every assertion below reads <see cref="FakeHubCapture.ReceivedRegister"/> —
/// the <c>Register</c> message as a hub parsed it off a real loopback HTTP/2
/// stream — never a property of the <see cref="ConnectorApp"/> and never the
/// <c>Register</c> object the test itself built. That distinction is the whole
/// point of this file: the declaration reached the app object for a full task
/// before anything ever emitted it, and the platform's schema-extraction path
/// was unreachable in production the entire time.
/// </summary>
[Collection("integration")]
public class RegisterRelationalSourceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Agent + the two tools a relational source must be able to name.</summary>
    private static FakeAssembly AssemblyWith(params Type[] extra) => new(
        new[]
        {
            typeof(RelationalDemoAgent),
            typeof(RelationalDescribeTool),
            typeof(RelationalQueryTool),
        }.Concat(extra).ToArray());

    // Build() refuses a credential handler with no private key. The shared
    // cross-SDK vector file already ships one.
    private static readonly string ConnectorPrivateKey = ReadConnectorPrivateKey();

    private static string ReadConnectorPrivateKey()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "testdata", "credential-envelope-vectors.json")));
        return doc.RootElement.GetProperty("connector_private_key_pkcs8_pem").GetString()!;
    }

    private static async Task<int> RunSupervisorAsync(ConnectorApp app, int port, CancellationToken testCt)
    {
        using var signals = new SignalHandler();
        using var reg = testCt.Register(() => signals.InternalCancelHook?.Invoke());

        return await Supervisor
            .RunAsync(app, "test-token", "127.0.0.1", port, insecure: true, signals)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // The round trip: both declarations arrive on ONE Register.

    [Fact(Timeout = 10_000)]
    public async Task Register_CarriesRelationalSourceAndCredentialSchema_ToTheHub()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            AcceptRegister    = true,
            ToolCalls         = Array.Empty<ScriptedToolCall>(),
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = ConnectorHost.CreateBuilder()
                .ScanAssembly(AssemblyWith(
                    typeof(RegisterLiveFingerprintProvider),
                    typeof(RegisterDemoCredentialHandler)))
                .UseCredentialKeys(ConnectorPrivateKey)
                .UseInsecureTransport()
                .Build();

            var exit = await RunSupervisorAsync(app, server.Port, cts.Token).ConfigureAwait(false);
            Assert.Equal(78, exit);   // GoAway("revoked")

            var register = server.Capture.ReceivedRegister;
            Assert.NotNull(register);

            // --- the declaration this task exists to emit ---
            Assert.NotNull(register.RelationalSource);
            Assert.Equal("sqlserver", register.RelationalSource.Engine);
            Assert.Equal("rs.demo.describe_schema", register.RelationalSource.DescribeTool);
            Assert.Equal("rs.demo.query_sql", register.RelationalSource.QueryTool);
            Assert.Equal("Sql", register.RelationalSource.SqlArg);

            // Read live from the provider at register time — not captured at
            // scan time, which is why RelationalSourceDeclaration carries none.
            Assert.Equal(
                RegisterLiveFingerprintProvider.CatalogFingerprint,
                register.RelationalSource.Fingerprint);

            // --- and the sibling declaration still arrives beside it ---
            // Nothing else proves the two coexist on one Register, and this
            // task changes the method that builds it.
            Assert.NotNull(register.CredentialSchema);
            Assert.Equal("basic", register.CredentialSchema.Kind);
            Assert.Equal("Demo ERP account", register.CredentialSchema.Title);
            Assert.Equal(
                new[] { "username", "password" },
                register.CredentialSchema.Fields.Select(f => f.Key).ToArray());
        });
    }

    // -----------------------------------------------------------------------
    // Declare nothing, stay untouched — asserted on the wire, not on the app.

    [Fact(Timeout = 10_000)]
    public async Task Register_ConnectorFrontingNoDatabase_CarriesNoRelationalSource()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            AcceptRegister    = true,
            ToolCalls         = Array.Empty<ScriptedToolCall>(),
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = ConnectorHost.CreateBuilder()
                .ScanAssembly(AssemblyWith())
                .UseInsecureTransport()
                .Build();

            await RunSupervisorAsync(app, server.Port, cts.Token).ConfigureAwait(false);

            var register = server.Capture.ReceivedRegister;
            Assert.NotNull(register);
            // Absence is the signal that keeps this connector out of schema
            // extraction and out of the SQL gate.
            Assert.Null(register.RelationalSource);
            Assert.Null(register.CredentialSchema);
        });
    }

    // -----------------------------------------------------------------------
    // Fingerprint unavailable — a normal, transient startup condition.

    [Fact(Timeout = 10_000)]
    public async Task Register_FingerprintUnavailable_StillCarriesTheDeclarationWithAnEmptyFingerprint()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            AcceptRegister = true,
            ToolCalls = new[]
            {
                new ScriptedToolCall
                {
                    ToolKey      = "rs.demo.query_sql",
                    ArgsJson     = """{"Sql":"select 1","Scope":"asg"}""",
                    InvocationId = "inv-after-register",
                },
            },
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = ConnectorHost.CreateBuilder()
                .ScanAssembly(AssemblyWith(typeof(RegisterUnreachableCatalogProvider)))
                .UseInsecureTransport()
                .Build();

            await RunSupervisorAsync(app, server.Port, cts.Token).ConfigureAwait(false);

            var register = server.Capture.ReceivedRegister;
            Assert.NotNull(register);

            // Present, not omitted: an empty fingerprint costs a re-extraction,
            // omitting the declaration would silently disable extraction and
            // governance for as long as the database stays down.
            Assert.NotNull(register.RelationalSource);
            Assert.Equal("sqlserver", register.RelationalSource.Engine);
            Assert.Equal("rs.demo.query_sql", register.RelationalSource.QueryTool);
            Assert.Equal("", register.RelationalSource.Fingerprint);

            // Registration still succeeded: a ToolCallResponse can only exist
            // if the daemon got past RegisterAck into steady state.
            var response = Assert.Single(server.Capture.ReceivedToolResponses);
            Assert.Equal("inv-after-register", response.InvocationId);
            Assert.Equal(ToolCallResponse.ResultOneofCase.ResultJson, response.ResultCase);
        });
    }

    // -----------------------------------------------------------------------
    // The warning that makes the empty fingerprint diagnosable.

    [Fact]
    public async Task ToProtoAsync_FingerprintUnavailable_WarnsNamingTheException()
    {
        // An empty fingerprint is otherwise indistinguishable from "the core
        // has never seen this catalog", so the log line is the only thing that
        // says a database was unreachable. Asserted through the injected sink
        // rather than Console.Error, which is process-global.
        var warnings = new List<string>();

        var decl = await Daemon.ToProtoAsync(
            new RelationalSourceDeclaration(
                "sqlserver", "rs.demo.describe_schema", "rs.demo.query_sql", "Sql",
                typeof(RegisterUnreachableCatalogProvider)),
            new RegisterUnreachableCatalogProvider(),
            CancellationToken.None,
            warnings.Add);

        Assert.Equal("", decl.Fingerprint);
        Assert.Equal("sqlserver", decl.Engine);

        var warning = Assert.Single(warnings);
        Assert.Contains(nameof(InvalidOperationException), warning);
        Assert.Contains(RegisterUnreachableCatalogProvider.Failure, warning);
    }
}

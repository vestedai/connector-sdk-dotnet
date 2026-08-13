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
/// A catalog that answers, but far too late — a cold SQL Server behind
/// SqlClient's own 15s-connect + 30s-per-command defaults. Honours its
/// cancellation token, as <see cref="ICatalogReader"/> requires and as the
/// SDK's own <see cref="SqlServerProvider"/> does.
/// </summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class RegisterSlowCatalogProvider : IRelationalSchemaProvider
{
    /// <summary>Far longer than any bound a test would set.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(40);

    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public async Task<string> CatalogFingerprintAsync(CancellationToken ct)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);
        return "a-fingerprint-nobody-should-ever-see";
    }
}

/// <summary>
/// Returns a DIFFERENT fingerprint on every call, so a test can tell a value
/// read live this session from one cached at <c>Build()</c> and replayed.
/// </summary>
[RelationalSource(
    Engine = "sqlserver",
    DescribeTool = "rs.demo.describe_schema",
    QueryTool = "rs.demo.query_sql",
    SqlArg = "Sql")]
public sealed class RegisterCountingFingerprintProvider : IRelationalSchemaProvider
{
    private int _calls;

    /// <summary>How many times the catalog has been hashed.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public Task<IReadOnlyList<string>> ScopesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<CanonicalSchema> DescribeAsync(string scopeKey, CancellationToken ct)
        => Task.FromResult(new CanonicalSchema(new List<CanonicalEntity>(), new List<CanonicalRelation>()));

    public Task<string> CatalogFingerprintAsync(CancellationToken ct)
        => Task.FromResult($"catalog-{Interlocked.Increment(ref _calls)}");
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

            var exit = await SessionRunner.RunSupervisorAsync(app, server.Port, cts.Token).ConfigureAwait(false);
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

            await SessionRunner.RunSupervisorAsync(app, server.Port, cts.Token).ConfigureAwait(false);

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

            await SessionRunner.RunSupervisorAsync(app, server.Port, cts.Token).ConfigureAwait(false);

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

        // The converse of the timeout test below: these two conditions send an
        // operator to different systems, so neither message may read as the
        // other. Here the database ANSWERED, with an error.
        Assert.DoesNotContain("did not answer within", warning);
    }

    // -----------------------------------------------------------------------
    // The bound. A fingerprint fetched before Register is sent must not be
    // allowed to outlast the hub's idle timer.

    [Fact(Timeout = 10_000)]
    public async Task Register_FingerprintOutlastsItsBound_StillRegistersWithAnEmptyFingerprint()
    {
        // Without a bound this is not a slow schema extraction, it is a dead
        // connector: the hub's idle timer runs from HelloAck with Register as
        // the next expected frame and the heartbeat not yet started, so it
        // sends GoAway{reason:"idle"}, the supervisor reconnects, and every
        // tool this connector exposes stays offline in a loop — logged only as
        // idleness, which names nothing about the fingerprint.
        //
        // The bound is injected rather than waited out; the delay/bound ratio
        // (40s vs 250ms) is the same shape as the real one (40s vs 10s).
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
                .ScanAssembly(AssemblyWith(typeof(RegisterSlowCatalogProvider)))
                .UseInsecureTransport()
                .Build();

            var started = System.Diagnostics.Stopwatch.StartNew();

            var (_, handshakeCompleted) = await SessionRunner
                .RunOneSessionAsync(app, server.Port, cts.Token,
                    fingerprintTimeout: TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);

            started.Stop();

            // The point of the bound. Asserted explicitly so a regression reads
            // as "Register waited for the catalog" rather than as a flaky
            // 10-second test timeout.
            Assert.True(
                started.Elapsed < TimeSpan.FromSeconds(5),
                $"Register waited {started.Elapsed.TotalSeconds:0.#}s on a provider that never " +
                $"answers; the fingerprint fetch is not bounded.");

            var register = server.Capture.ReceivedRegister;
            Assert.NotNull(register);
            Assert.NotNull(register.RelationalSource);
            Assert.Equal("sqlserver", register.RelationalSource.Engine);
            Assert.Equal("rs.demo.query_sql", register.RelationalSource.QueryTool);
            Assert.Equal("", register.RelationalSource.Fingerprint);

            // Registration still succeeded — HandshakeCompleted is set only
            // after an "accepted" RegisterAck.
            Assert.True(handshakeCompleted);
        });
    }

    [Fact]
    public async Task ToProtoAsync_FingerprintOutlastsItsBound_WarnsAboutTheWaitNotAnError()
    {
        var warnings = new List<string>();

        var decl = await Daemon.ToProtoAsync(
            new RelationalSourceDeclaration(
                "sqlserver", "rs.demo.describe_schema", "rs.demo.query_sql", "Sql",
                typeof(RegisterSlowCatalogProvider)),
            new RegisterSlowCatalogProvider(),
            CancellationToken.None,
            warnings.Add,
            TimeSpan.FromMilliseconds(50));

        Assert.Equal("", decl.Fingerprint);

        var warning = Assert.Single(warnings);
        Assert.Contains("did not answer within", warning);

        // An operator must not read a silent database as a failing one: chasing
        // a driver error that never happened is the wrong investigation. The
        // cancellation is an implementation detail of the bound, not a fault
        // the database reported.
        Assert.DoesNotContain("Exception", warning);
    }

    // -----------------------------------------------------------------------
    // Read live, per session — the claim BuildRegisterAsync's docblock makes.

    [Fact(Timeout = 10_000)]
    public async Task Register_ReadsTheFingerprintOncePerSession_NotOnceAtBuild()
    {
        // An implementation that hashed the catalog at Build() and replayed the
        // value would pass every other test in this file, and would report a
        // stale hash forever after a schema change — leaving the core certain
        // it need not re-extract. The provider returns a different value per
        // call, so a cached one is visible as the WRONG value, not merely as a
        // call count.
        using var cts = new CancellationTokenSource(TestTimeout);

        var provider = new RegisterCountingFingerprintProvider();

        var app = ConnectorHost.CreateBuilder()
            .ScanAssembly(AssemblyWith())
            .UseRelationalSchemaProvider(provider)
            .UseInsecureTransport()
            .Build();

        var script = new FakeHubScript
        {
            AcceptRegister    = true,
            ToolCalls         = Array.Empty<ScriptedToolCall>(),
            FinalGoAwayReason = "revoked",
        };

        // Two sessions of the same connector — what the supervisor does across
        // a reconnect, minus the backoff wait.
        var first = await FakeHubServer.RunAsync(script, async server =>
        {
            await SessionRunner.RunOneSessionAsync(app, server.Port, cts.Token).ConfigureAwait(false);
            return server.Capture.ReceivedRegister;
        });

        var second = await FakeHubServer.RunAsync(script, async server =>
        {
            await SessionRunner.RunOneSessionAsync(app, server.Port, cts.Token).ConfigureAwait(false);
            return server.Capture.ReceivedRegister;
        });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(first.RelationalSource);
        Assert.NotNull(second.RelationalSource);

        Assert.Equal("catalog-1", first.RelationalSource.Fingerprint);
        Assert.Equal("catalog-2", second.RelationalSource.Fingerprint);
        Assert.Equal(2, provider.Calls);
    }
}

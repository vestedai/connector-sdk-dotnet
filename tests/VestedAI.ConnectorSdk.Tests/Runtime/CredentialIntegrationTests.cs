using System.Text.Json;
using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Credential;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Tests.Fixtures;
using VestedAI.ConnectorSdk.Tool;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

// ---------------------------------------------------------------------------
// End-to-end proof that per-user credentials work through the real runtime:
// Hello → HelloAck (connector id) → Register (credential_schema) →
// CredentialOpRequest → CredentialOpResponse → ToolCallRequest carrying a
// sealed envelope → tool reads ctx.Credential().
//
// Every step here was unreachable in 0.4.2: the builder could not register a
// handler, Register never carried the schema, and Supervisor passed neither the
// opener nor the op dispatcher.
// ---------------------------------------------------------------------------

[Agent(Key = "c.test", Name = "CredentialTestAgent", Model = "openai:gpt-4o",
       Description = "Agent fixture for credential integration tests.")]
public class CredentialTestAgent { }

/// <summary>Returns the calling user's credential fields, proving the tool can read them.</summary>
[Tool(Key = "c.test.whoami",
      Description = "Return the caller's credential user name.",
      Sensitivity = "read")]
public class WhoAmITool : ToolHandler<WhoAmITool.Args, WhoAmITool.Result>
{
    public class Args { }

    public class Result
    {
        public bool HadCredential { get; set; }
        public string Username { get; set; } = "";
    }

    public override Task<Result> HandleAsync(Args args, ToolContext ctx)
    {
        if (!ctx.HasCredential())
            return Task.FromResult(new Result { HadCredential = false });

        var cred = ctx.Credential();
        return Task.FromResult(new Result
        {
            HadCredential = true,
            Username      = cred["username"],
        });
    }
}

[Collection("integration")]
public class CredentialIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonElement Fixture = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "testdata", "credential-envelope-vectors.json")))
        .RootElement;

    private static JsonElement Vector => Fixture.GetProperty("vectors")[0];
    private static string ConnectorKey =>
        Fixture.GetProperty("connector_private_key_pkcs8_pem").GetString()!;
    private static string ConnectorId => Vector.GetProperty("connector_id").GetString()!;
    private static string UserId => Vector.GetProperty("user_id").GetString()!;
    private static string EnvelopeJson => Vector.GetProperty("envelope").GetRawText();

    private static readonly FakeAssembly CredentialAssembly = new(
        typeof(CredentialTestAgent),
        typeof(WhoAmITool),
        typeof(DemoCredentialHandler));

    private static ConnectorApp BuildApp()
        => ConnectorHost.CreateBuilder()
            .ScanAssembly(CredentialAssembly)
            .UseCredentialKeys(ConnectorKey)
            .UseInsecureTransport()
            .Build();

    private static async Task<int> RunSupervisorAsync(
        ConnectorApp app, int port, CancellationToken testCt)
    {
        using var signals = new SignalHandler();
        using var reg = testCt.Register(() => signals.InternalCancelHook?.Invoke());

        return await Supervisor
            .RunAsync(app, "test-token", "127.0.0.1", port, insecure: true, signals)
            .ConfigureAwait(false);
    }

    [Fact(Timeout = 10_000)]
    public async Task FullCredentialRoundtrip_SchemaRegistered_OpAnswered_ToolReadsCredential()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            // The envelope is sealed for this connector id, so the hub must
            // hand out the id the vector was built with.
            ConnectorId = ConnectorId,
            CredentialOps = new[]
            {
                new ScriptedCredentialOp
                {
                    OpId         = "op-validate-1",
                    Op           = "validate",
                    UserId       = UserId,
                    UserEmail    = "j.smith@example.com",
                    EnvelopeJson = EnvelopeJson,
                },
            },
            ToolCalls = new[]
            {
                new ScriptedToolCall
                {
                    ToolKey                = "c.test.whoami",
                    ArgsJson               = "{}",
                    InvocationId           = "inv-cred-1",
                    UserId                 = UserId,
                    CredentialEnvelopeJson = EnvelopeJson,
                },
            },
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var exitCode = await RunSupervisorAsync(BuildApp(), server.Port, cts.Token)
                .ConfigureAwait(false);

            Assert.Equal(78, exitCode);

            var cap = server.Capture;

            // Gap 3: the schema reaches the hub at Register.
            Assert.NotNull(cap.ReceivedRegister);
            var schema = cap.ReceivedRegister.CredentialSchema;
            Assert.NotNull(schema);
            Assert.Equal("basic", schema.Kind);
            Assert.Equal("Demo ERP account", schema.Title);
            Assert.Collection(schema.Fields,
                f => { Assert.Equal("username", f.Key); Assert.Equal("text", f.Type); },
                f => { Assert.Equal("password", f.Key); Assert.Equal("password", f.Type); });

            // Gaps 4 + 5: the op is answered, and answered correctly — which
            // only happens if the connector id was resolved after HelloAck.
            var opResp = Assert.Single(cap.ReceivedCredentialResponses);
            Assert.Equal("op-validate-1", opResp.OpId);
            Assert.True(opResp.Ok, opResp.Error);
            Assert.Equal("j.smith", opResp.Display.Fields["account"].StringValue);

            // Gaps 1 + 2 + 4: the tool actually opened the envelope.
            var toolResp = Assert.Single(cap.ReceivedToolResponses);
            Assert.Equal("inv-cred-1", toolResp.InvocationId);
            Assert.True(string.IsNullOrEmpty(toolResp.Error), toolResp.Error);

            using var doc = JsonDocument.Parse(toolResp.ResultJson.ToStringUtf8());
            Assert.True(doc.RootElement.GetProperty("HadCredential").GetBoolean());
            Assert.Equal("j.smith", doc.RootElement.GetProperty("Username").GetString());
        });
    }

    [Fact(Timeout = 10_000)]
    public async Task EnvelopeSealedForAnotherUser_ToolCallFails_AndLeaksNothing()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            ConnectorId = ConnectorId,
            ToolCalls = new[]
            {
                new ScriptedToolCall
                {
                    ToolKey                = "c.test.whoami",
                    ArgsJson               = "{}",
                    InvocationId           = "inv-cred-mismatch",
                    // Same envelope, different user — the AAD binding must reject it.
                    UserId                 = "9999",
                    CredentialEnvelopeJson = EnvelopeJson,
                },
            },
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            await RunSupervisorAsync(BuildApp(), server.Port, cts.Token).ConfigureAwait(false);

            var resp = Assert.Single(server.Capture.ReceivedToolResponses);
            Assert.False(string.IsNullOrEmpty(resp.Error),
                "an envelope sealed for another user must not resolve");
            Assert.DoesNotContain("s3cr3t", resp.Error, StringComparison.Ordinal);
        });
    }

    [Fact(Timeout = 10_000)]
    public async Task NoCredentialSchema_RegisterOmitsIt_AndToolsStillRun()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // The BC-connector shape: agents and tools, no credential handler.
        var plainAssembly = new FakeAssembly(typeof(CredentialTestAgent), typeof(WhoAmITool));

        var script = new FakeHubScript
        {
            ToolCalls = new[]
            {
                new ScriptedToolCall
                {
                    ToolKey      = "c.test.whoami",
                    ArgsJson     = "{}",
                    InvocationId = "inv-plain-1",
                },
            },
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = ConnectorHost.CreateBuilder()
                .ScanAssembly(plainAssembly)
                .UseInsecureTransport()
                .Build();

            await RunSupervisorAsync(app, server.Port, cts.Token).ConfigureAwait(false);

            var cap = server.Capture;

            // Absent schema is what tells the platform never to gate this connector.
            Assert.NotNull(cap.ReceivedRegister);
            Assert.Null(cap.ReceivedRegister.CredentialSchema);

            // And the tool runs exactly as before, seeing no credential.
            var resp = Assert.Single(cap.ReceivedToolResponses);
            Assert.True(string.IsNullOrEmpty(resp.Error), resp.Error);

            using var doc = JsonDocument.Parse(resp.ResultJson.ToStringUtf8());
            Assert.False(doc.RootElement.GetProperty("HadCredential").GetBoolean());
        });
    }
}

using VestedAI.ConnectorSdk.Tests.Fixtures;
using VestedAI.ConnectorSdk.Runtime;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

/// <summary>
/// Lightweight tests for the Daemon via the full supervisor+fake-hub stack.
///
/// These complement the granular <see cref="SupervisorTests"/> (which use a
/// stub IDaemonFactory) by exercising the real Daemon's handshake path against
/// the in-process gRPC server.  Tests are kept small so they run fast (≤10 s).
/// </summary>
[Collection("integration")]
public class DaemonTests
{
    // Hermetic assembly — only the integration fixture types (avoids BogusToolHandler).
    private static readonly FakeAssembly IntegrationAssembly = new(
        typeof(IntegrationTestAgent),
        typeof(IntegrationEchoTool));

    private static ConnectorApp BuildApp()
        => ConnectorHost.CreateBuilder()
            .ScanAssembly(IntegrationAssembly)
            .UseInsecureTransport()
            .Build();

    // ---------------------------------------------------------------------------
    // Handshake: Hello + Register sent; RegisterAck accepted → HandshakeCompleted.
    //
    // We verify via the captured Register (non-null + non-empty fingerprint) rather
    // than exposing HandshakeCompleted through the public API.

    [Fact(Timeout = 10_000)]
    public async Task Handshake_HelloAndRegisterSent_RegisterAccepted()
    {
        var script = new FakeHubScript
        {
            AcceptRegister    = true,
            ToolCalls         = Array.Empty<ScriptedToolCall>(),
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = BuildApp();
            using var signals = new SignalHandler();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var reg = cts.Token.Register(() => signals.InternalCancelHook?.Invoke());

            int exit = await Supervisor.RunAsync(
                app, "tok", "127.0.0.1", server.Port, insecure: true, signals)
                .ConfigureAwait(false);

            // GoAway("revoked") terminates with 78
            Assert.Equal(78, exit);

            // Hello was sent
            Assert.NotNull(server.Capture.ReceivedHello);
            Assert.Equal("dotnet", server.Capture.ReceivedHello.SdkLanguage);

            // Register was sent with a non-empty fingerprint
            Assert.NotNull(server.Capture.ReceivedRegister);
            Assert.False(
                string.IsNullOrEmpty(server.Capture.ReceivedRegister.BaselineFingerprint),
                "baseline_fingerprint must not be empty");
        });
    }

    // ---------------------------------------------------------------------------
    // Register rejected → Daemon returns TokenException → supervisor exits 78.

    [Fact(Timeout = 10_000)]
    public async Task RegisterRejected_DaemonExits78()
    {
        var script = new FakeHubScript
        {
            AcceptRegister       = false,
            RegisterRejectReason = "daemon test rejection",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = BuildApp();
            using var signals = new SignalHandler();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var reg = cts.Token.Register(() => signals.InternalCancelHook?.Invoke());

            int exit = await Supervisor.RunAsync(
                app, "tok", "127.0.0.1", server.Port, insecure: true, signals)
                .ConfigureAwait(false);

            Assert.Equal(78, exit);
        });
    }

    // ---------------------------------------------------------------------------
    // Hello contains correct SDK fields.

    [Fact(Timeout = 10_000)]
    public async Task Hello_ContainsCorrectSdkFields()
    {
        var script = new FakeHubScript
        {
            AcceptRegister    = true,
            ToolCalls         = Array.Empty<ScriptedToolCall>(),
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = BuildApp();
            using var signals = new SignalHandler();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var reg = cts.Token.Register(() => signals.InternalCancelHook?.Invoke());

            await Supervisor.RunAsync(
                app, "tok", "127.0.0.1", server.Port, insecure: true, signals)
                .ConfigureAwait(false);

            var hello = server.Capture.ReceivedHello;
            Assert.NotNull(hello);
            Assert.Equal("dotnet", hello.SdkLanguage);
            Assert.Equal(VestedAI.ConnectorSdk.SdkInfo.Version, hello.SdkVersion);
            // worker_id = "MachineName:PID"
            Assert.Contains(":", hello.WorkerId);
        });
    }

    // ---------------------------------------------------------------------------
    // Register contains tool sensitivity correctly.

    [Fact(Timeout = 10_000)]
    public async Task Register_ToolSensitivity_RidesThroughToHub()
    {
        var script = new FakeHubScript
        {
            AcceptRegister    = true,
            ToolCalls         = Array.Empty<ScriptedToolCall>(),
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = BuildApp();
            using var signals = new SignalHandler();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var reg = cts.Token.Register(() => signals.InternalCancelHook?.Invoke());

            await Supervisor.RunAsync(
                app, "tok", "127.0.0.1", server.Port, insecure: true, signals)
                .ConfigureAwait(false);

            var register = server.Capture.ReceivedRegister;
            Assert.NotNull(register);

            // Find the t.test agent
            var agent = register.Agents.FirstOrDefault(a => a.Key == "t.test");
            Assert.NotNull(agent);

            // Find the t.test.echo tool
            var tool = agent.Tools.FirstOrDefault(t => t.Key == "t.test.echo");
            Assert.NotNull(tool);

            // Sensitivity declared as "destructive" in IntegrationEchoTool
            Assert.Equal("destructive", tool.Sensitivity);
        });
    }
}

using System.Reflection;
using System.Text.Json;
using VestedAI.ConnectorSdk.Tests.Fixtures;
using VestedAI.ConnectorSdk.Runtime;
using Vested.V1;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

/// <summary>
/// Full end-to-end integration tests for the connector daemon.
///
/// Each test spins up a scriptable in-process Kestrel gRPC server
/// (<see cref="FakeHubServer"/>) that speaks the ConnectorHub protocol over
/// plain HTTP/2 (h2c) on a loopback port, then runs the
/// <see cref="Supervisor"/> against it.
///
/// Covers:
///   1. Full roundtrip: Hello → Register → ToolCallRequest → ToolCallResponse
///      → GoAway("revoked") → exit 78, with sensitivity "destructive" asserted.
///   2. Register rejected → exit 78.
///   3. GoAway("revoked") with no tool calls → exit 78.
/// </summary>
[Collection("integration")]
public class IntegrationTests
{
    // Per-test timeout: 10 seconds — the server and channel add overhead.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    // ---------------------------------------------------------------------------
    // Helper: build a ConnectorApp from the test-assembly fixtures.

    private static ConnectorApp BuildApp()
        => ConnectorHost.CreateBuilder()
            .ScanAssembly(Assembly.GetExecutingAssembly())
            .UseInsecureTransport()
            .Build();

    // Helper: run Supervisor against a loopback fake hub with a short timeout
    // so tests don't hang if something goes wrong.
    private static async Task<int> RunSupervisorAsync(
        ConnectorApp app,
        int port,
        CancellationToken testCt)
    {
        using var signals = new SignalHandler();

        // If the test timeout fires, cancel the supervisor too.
        using var reg = testCt.Register(() => signals.InternalCancelHook?.Invoke());

        return await Supervisor.RunAsync(app, "test-token", "127.0.0.1", port, insecure: true, signals)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Test 1: Full roundtrip — assert exit 78 + sensitivity + tool response shape.

    [Fact(Timeout = 10_000)]
    public async Task FullRoundtrip_EchoTool_Revoked_Returns78()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            AcceptRegister = true,
            ToolCalls = new[]
            {
                new ScriptedToolCall
                {
                    ToolKey        = "t.test.echo",
                    ArgsJson       = """{"text":"hello from fake hub"}""",
                    InvocationId   = "inv-roundtrip-1",
                },
            },
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = BuildApp();
            var exitCode = await RunSupervisorAsync(app, server.Port, cts.Token)
                .ConfigureAwait(false);

            // GoAway("revoked") → supervisor exits 78
            Assert.Equal(78, exitCode);

            var cap = server.Capture;

            // Hello was sent
            Assert.NotNull(cap.ReceivedHello);
            Assert.Equal("dotnet", cap.ReceivedHello.SdkLanguage);
            Assert.False(string.IsNullOrEmpty(cap.ReceivedHello.SdkVersion));

            // Register was sent
            Assert.NotNull(cap.ReceivedRegister);
            Assert.False(string.IsNullOrEmpty(cap.ReceivedRegister.BaselineFingerprint),
                "baseline_fingerprint must be non-empty (Python v0.2.1 fix)");

            // The agent "t.test" is in Register
            var agent = cap.ReceivedRegister.Agents
                .FirstOrDefault(a => a.Key == "t.test");
            Assert.NotNull(agent);

            // The tool "t.test.echo" is under the agent with sensitivity "destructive"
            var tool = agent.Tools.FirstOrDefault(t => t.Key == "t.test.echo");
            Assert.NotNull(tool);
            Assert.Equal("destructive", tool.Sensitivity);

            // Exactly one ToolCallResponse was captured
            Assert.Single(cap.ReceivedToolResponses);

            var resp = cap.ReceivedToolResponses[0];
            Assert.Equal("inv-roundtrip-1", resp.InvocationId);
            Assert.Equal(ToolCallResponse.ResultOneofCase.ResultJson, resp.ResultCase);

            var resultJson = resp.ResultJson.ToStringUtf8();
            using var doc = JsonDocument.Parse(resultJson);
            var echoed = doc.RootElement.GetProperty("Echoed").GetString();
            Assert.Equal("hello from fake hub", echoed);
        });
    }

    // ---------------------------------------------------------------------------
    // Test 2: Register rejected → exit 78 immediately.

    [Fact(Timeout = 10_000)]
    public async Task RegisterRejected_Returns78()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            AcceptRegister      = false,
            RegisterRejectReason = "token revoked by test",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = BuildApp();
            var exitCode = await RunSupervisorAsync(app, server.Port, cts.Token)
                .ConfigureAwait(false);

            Assert.Equal(78, exitCode);

            // Register was still captured (hub received it before rejecting)
            Assert.NotNull(server.Capture.ReceivedRegister);
            // No tool responses (session ended at RegisterAck)
            Assert.Empty(server.Capture.ReceivedToolResponses);
        });
    }

    // ---------------------------------------------------------------------------
    // Test 3: GoAway("revoked") with no tool calls → exit 78.

    [Fact(Timeout = 10_000)]
    public async Task GoAway_Revoked_NoToolCalls_Returns78()
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
            var app = BuildApp();
            var exitCode = await RunSupervisorAsync(app, server.Port, cts.Token)
                .ConfigureAwait(false);

            Assert.Equal(78, exitCode);
            Assert.Empty(server.Capture.ReceivedToolResponses);
        });
    }

    // ---------------------------------------------------------------------------
    // Test 4: GoAway("shutdown") → not revoked → supervisor reconnects.
    //
    // The supervisor will try to reconnect after GoAway(shutdown), so we need to
    // orchestrate two server interactions: first session ends with GoAway("shutdown"),
    // then signal the supervisor to stop.  We test this by racing the supervisor
    // against a timer-fired cancellation after the first GoAway arrives.

    [Fact(Timeout = 10_000)]
    public async Task GoAway_Shutdown_NotRevoked_SupervisorReconnects()
    {
        // We can't easily test the full reconnect loop in-process without a more
        // complex multi-session fake.  Instead, we verify the exit code is NOT 78
        // (i.e. not the permanent-failure path) and the supervisor exits cleanly
        // via cancellation signal after GoAway("shutdown").
        //
        // The test runs two back-to-back servers on *different* ports.  The
        // supervisor will try to reconnect after the first session ends; the second
        // server is not started before the signal fires, so the supervisor's backoff
        // delay is interrupted by the signal and it exits 0.
        //
        // This is a lightweight surrogate for the full reconnect-loop test which is
        // already covered by SupervisorTests.TransientError_KeepsLooping_UntilSignal.

        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            AcceptRegister    = true,
            ToolCalls         = Array.Empty<ScriptedToolCall>(),
            FinalGoAwayReason = "shutdown",   // NOT revoked → transient
        };

        int exitCode;
        await using var server = await FakeHubServer.StartAsync(script).ConfigureAwait(false);

        using var signals = new SignalHandler();
        using var reg = cts.Token.Register(() => signals.InternalCancelHook?.Invoke());

        // Start the supervisor in background.
        var supervisorTask = Supervisor.RunAsync(
            BuildApp(), "test-token", "127.0.0.1", server.Port, insecure: true, signals);

        // Wait for the server to receive the Register (handshake done, GoAway is next).
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (server.Capture.ReceivedRegister is null && DateTime.UtcNow < deadline)
            await Task.Delay(20).ConfigureAwait(false);

        Assert.NotNull(server.Capture.ReceivedRegister);

        // Give the supervisor a moment to process GoAway then fire the signal
        // so the backoff-wait is interrupted and the supervisor exits 0.
        await Task.Delay(200).ConfigureAwait(false);
        signals.InternalCancelHook?.Invoke();

        exitCode = await supervisorTask.ConfigureAwait(false);
        Assert.Equal(0, exitCode);
    }

    // ---------------------------------------------------------------------------
    // Test 5: Multiple tool calls in sequence — all responses captured.

    [Fact(Timeout = 10_000)]
    public async Task MultipleToolCalls_AllResponsesCaptured()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        var script = new FakeHubScript
        {
            AcceptRegister = true,
            ToolCalls = new[]
            {
                new ScriptedToolCall
                {
                    ToolKey = "t.test.echo", ArgsJson = """{"text":"first"}""",
                    InvocationId = "inv-multi-1",
                },
                new ScriptedToolCall
                {
                    ToolKey = "t.test.echo", ArgsJson = """{"text":"second"}""",
                    InvocationId = "inv-multi-2",
                },
                new ScriptedToolCall
                {
                    ToolKey = "t.test.echo", ArgsJson = """{"text":"third"}""",
                    InvocationId = "inv-multi-3",
                },
            },
            FinalGoAwayReason = "revoked",
        };

        await FakeHubServer.RunAsync(script, async server =>
        {
            var app = BuildApp();
            var exitCode = await RunSupervisorAsync(app, server.Port, cts.Token)
                .ConfigureAwait(false);

            Assert.Equal(78, exitCode);
            Assert.Equal(3, server.Capture.ReceivedToolResponses.Count);

            var ids = server.Capture.ReceivedToolResponses.Select(r => r.InvocationId).ToList();
            Assert.Contains("inv-multi-1", ids);
            Assert.Contains("inv-multi-2", ids);
            Assert.Contains("inv-multi-3", ids);

            foreach (var resp in server.Capture.ReceivedToolResponses)
            {
                Assert.Equal(ToolCallResponse.ResultOneofCase.ResultJson, resp.ResultCase);
            }
        });
    }
}

// ---------------------------------------------------------------------------
// xUnit collection definition — keeps integration tests on a single thread
// to avoid port-binding races.

[CollectionDefinition("integration", DisableParallelization = true)]
public class IntegrationCollection { }

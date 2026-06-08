using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vested.V1;

namespace VestedAI.ConnectorSdk.Tests.Fixtures;

// ---------------------------------------------------------------------------
// Script model
// ---------------------------------------------------------------------------

/// <summary>
/// One scripted tool invocation the fake hub will send to the connector.
/// </summary>
public sealed class ScriptedToolCall
{
    public string ToolKey { get; init; } = "";
    public string ArgsJson { get; init; } = "{}";
    public string InvocationId { get; init; } = "inv-1";
}

/// <summary>
/// Drives a <see cref="FakeHubService"/> session.
/// </summary>
public sealed class FakeHubScript
{
    /// <summary>When false the hub rejects Register with TOKEN_ERROR. Default: true.</summary>
    public bool AcceptRegister { get; init; } = true;

    /// <summary>Reason string placed in the DeclIssue when AcceptRegister is false.</summary>
    public string RegisterRejectReason { get; init; } = "rejected by script";

    /// <summary>Ordered tool calls the hub issues after RegisterAck.</summary>
    public IReadOnlyList<ScriptedToolCall> ToolCalls { get; init; } =
        Array.Empty<ScriptedToolCall>();

    /// <summary>
    /// Reason sent in the final GoAway. Use "" to skip GoAway entirely.
    /// Default: "shutdown".
    /// </summary>
    public string FinalGoAwayReason { get; init; } = "shutdown";
}

// ---------------------------------------------------------------------------
// Captured state
// ---------------------------------------------------------------------------

/// <summary>
/// Captures everything the daemon sent to the fake hub during the session.
/// </summary>
public sealed class FakeHubCapture
{
    public Hello? ReceivedHello { get; internal set; }
    public Register? ReceivedRegister { get; internal set; }
    public List<ToolCallResponse> ReceivedToolResponses { get; } = new();
}

// ---------------------------------------------------------------------------
// FakeHubService — the gRPC service implementation
// ---------------------------------------------------------------------------

/// <summary>
/// Scriptable in-process implementation of <see cref="ConnectorHub.ConnectorHubBase"/>.
/// Hosted on a Kestrel test server so the daemon connects over a real loopback channel.
/// </summary>
internal sealed class FakeHubService : ConnectorHub.ConnectorHubBase
{
    private readonly FakeHubScript _script;
    private readonly FakeHubCapture _capture;

    public FakeHubService(FakeHubScript script, FakeHubCapture capture)
    {
        _script = script;
        _capture = capture;
    }

    public override async Task Connect(
        IAsyncStreamReader<ConnectorMsg> requestStream,
        IServerStreamWriter<HubMsg> responseStream,
        ServerCallContext context)
    {
        // Step 1: await Hello → send HelloAck
        await AwaitHello(requestStream, responseStream, context.CancellationToken)
            .ConfigureAwait(false);

        // Step 2: await Register → send RegisterAck (accepted or rejected per script)
        bool accepted = await AwaitRegister(requestStream, responseStream, context.CancellationToken)
            .ConfigureAwait(false);

        if (!accepted) return;

        // Step 3: issue each scripted tool call and await the ToolCallResponse
        foreach (var scripted in _script.ToolCalls)
        {
            await IssueToolCall(scripted, requestStream, responseStream, context.CancellationToken)
                .ConfigureAwait(false);
        }

        // Step 4: send GoAway if specified
        if (!string.IsNullOrEmpty(_script.FinalGoAwayReason))
        {
            await responseStream.WriteAsync(new HubMsg
            {
                GoAway = new GoAway { Reason = _script.FinalGoAwayReason }
            }).ConfigureAwait(false);
        }
        // gRPC server stream ends when Connect() returns.
    }

    // -----------------------------------------------------------------------
    // Steps

    private async Task AwaitHello(
        IAsyncStreamReader<ConnectorMsg> requestStream,
        IServerStreamWriter<HubMsg> responseStream,
        CancellationToken ct)
    {
        while (await requestStream.MoveNext(ct).ConfigureAwait(false))
        {
            var msg = requestStream.Current;
            if (msg.BodyCase == ConnectorMsg.BodyOneofCase.Hello)
            {
                _capture.ReceivedHello = msg.Hello;
                await responseStream.WriteAsync(new HubMsg
                {
                    HelloAck = new HelloAck
                    {
                        ConnectorId            = "fake-hub",
                        OrganizationId         = "test-org",
                        Namespace              = "test",
                        MaxAgents              = 10,
                        MaxToolsPerAgent       = 50,
                        MaxConcurrentToolCalls = 5,
                    }
                }, ct).ConfigureAwait(false);
                return;
            }
            // Absorb unexpected messages before Hello
        }
        throw new RpcException(new Status(StatusCode.Aborted, "stream ended before Hello"));
    }

    private async Task<bool> AwaitRegister(
        IAsyncStreamReader<ConnectorMsg> requestStream,
        IServerStreamWriter<HubMsg> responseStream,
        CancellationToken ct)
    {
        while (await requestStream.MoveNext(ct).ConfigureAwait(false))
        {
            var msg = requestStream.Current;

            if (msg.BodyCase == ConnectorMsg.BodyOneofCase.Heartbeat)
            {
                // Daemon may send a Heartbeat before Register — absorb it.
                continue;
            }

            if (msg.BodyCase == ConnectorMsg.BodyOneofCase.Register)
            {
                _capture.ReceivedRegister = msg.Register;

                if (_script.AcceptRegister)
                {
                    await responseStream.WriteAsync(new HubMsg
                    {
                        RegisterAck = new RegisterAck
                        {
                            BaselineFingerprint = "",
                            Status              = "accepted",
                        }
                    }, ct).ConfigureAwait(false);
                    return true;
                }
                else
                {
                    await responseStream.WriteAsync(new HubMsg
                    {
                        RegisterAck = new RegisterAck
                        {
                            BaselineFingerprint = "",
                            Status              = "rejected",
                            Issues =
                            {
                                new DeclIssue
                                {
                                    Path    = "",
                                    Code    = "TOKEN_ERROR",
                                    Message = _script.RegisterRejectReason,
                                }
                            },
                        }
                    }, ct).ConfigureAwait(false);
                    return false;
                }
            }
            // Absorb unexpected messages before Register
        }
        throw new RpcException(new Status(StatusCode.Aborted, "stream ended before Register"));
    }

    private async Task IssueToolCall(
        ScriptedToolCall scripted,
        IAsyncStreamReader<ConnectorMsg> requestStream,
        IServerStreamWriter<HubMsg> responseStream,
        CancellationToken ct)
    {
        // Send the ToolCallRequest
        await responseStream.WriteAsync(new HubMsg
        {
            ToolCallRequest = new ToolCallRequest
            {
                InvocationId   = scripted.InvocationId,
                ToolKey        = scripted.ToolKey,
                AgentKey       = "",
                ArgsJson       = Google.Protobuf.ByteString.CopyFromUtf8(scripted.ArgsJson),
                OrganizationId = "",
                UserId         = "",
                ConversationId = "",
                DeadlineMs     = 30_000,
                UserEmail      = "",
            }
        }, ct).ConfigureAwait(false);

        // Await the matching ToolCallResponse (absorb heartbeats)
        while (await requestStream.MoveNext(ct).ConfigureAwait(false))
        {
            var msg = requestStream.Current;

            if (msg.BodyCase == ConnectorMsg.BodyOneofCase.Heartbeat)
            {
                // Ack the heartbeat so the daemon's heartbeat timer doesn't stall.
                await responseStream.WriteAsync(new HubMsg { HeartbeatAck = new HeartbeatAck() }, ct)
                    .ConfigureAwait(false);
                continue;
            }

            if (msg.BodyCase == ConnectorMsg.BodyOneofCase.ToolCallResponse)
            {
                _capture.ReceivedToolResponses.Add(msg.ToolCallResponse);
                return;
            }
        }
        throw new RpcException(new Status(StatusCode.Aborted,
            $"stream ended before ToolCallResponse for {scripted.InvocationId}"));
    }
}

// ---------------------------------------------------------------------------
// FakeHubServer — Kestrel host wrapper
// ---------------------------------------------------------------------------

/// <summary>
/// Hosts a <see cref="FakeHubService"/> on a Kestrel HTTP/2 cleartext (h2c) loopback
/// port so the connector daemon can connect to it via <c>http://127.0.0.1:{Port}</c>.
///
/// Use via the static <see cref="RunAsync"/> helper or as an
/// <see cref="IAsyncDisposable"/> directly.
/// </summary>
public sealed class FakeHubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly FakeHubCapture _capture;

    private FakeHubServer(WebApplication app, FakeHubCapture capture)
    {
        _app = app;
        _capture = capture;
    }

    /// <summary>The loopback port the server is listening on.</summary>
    public int Port { get; private set; }

    /// <summary>State captured from the daemon during the session.</summary>
    public FakeHubCapture Capture => _capture;

    /// <summary>
    /// Creates and starts a fake hub server with the given script.
    /// Caller must dispose.
    /// </summary>
    public static async Task<FakeHubServer> StartAsync(FakeHubScript script)
    {
        var capture = new FakeHubCapture();

        var builder = WebApplication.CreateBuilder();

        // Suppress Microsoft framework logs that would flood test output.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Register the FakeHubService with its dependencies.
        builder.Services.AddSingleton(script);
        builder.Services.AddSingleton(capture);
        builder.Services.AddGrpc();

        // Kestrel: h2c (plaintext HTTP/2) on a random loopback port.
        // ListenLocalhost(0) does NOT support dynamic port assignment; use
        // Listen(IPAddress.Loopback, 0) instead (assigns OS port 0 → random).
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, lo =>
            {
                lo.Protocols = HttpProtocols.Http2;
            });
        });

        var app = builder.Build();
        app.MapGrpcService<FakeHubService>();

        await app.StartAsync().ConfigureAwait(false);

        // Resolve the bound port from the server's addresses.
        var addresses = app.Urls;
        int port = 0;
        foreach (var addr in addresses)
        {
            var uri = new Uri(addr);
            port = uri.Port;
            break;
        }

        return new FakeHubServer(app, capture) { Port = port };
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Convenience: run body against a fresh server, then dispose.

    /// <summary>
    /// Starts a fake hub, runs <paramref name="body"/>, then shuts down the server.
    /// </summary>
    public static async Task RunAsync(FakeHubScript script, Func<FakeHubServer, Task> body)
    {
        await using var server = await StartAsync(script).ConfigureAwait(false);
        await body(server).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a fake hub, runs <paramref name="body"/> and returns its result,
    /// then shuts down the server.
    /// </summary>
    public static async Task<T> RunAsync<T>(FakeHubScript script, Func<FakeHubServer, Task<T>> body)
    {
        await using var server = await StartAsync(script).ConfigureAwait(false);
        return await body(server).ConfigureAwait(false);
    }
}

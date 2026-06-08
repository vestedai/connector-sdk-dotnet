using Google.Protobuf;
using Vested.V1;
using VestedAI.ConnectorSdk.Errors;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// One connector session: Hello → HelloAck → Register → RegisterAck → steady-state.
/// Port of vested_connect/runtime/daemon.py and node/runtime/daemon.ts.
///
/// Exit codes:
///   0  — signal-driven graceful exit (signals.ShouldExit)
///   78 — token rejected / register rejected (EX_CONFIG)
///   1  — any other transient error
/// </summary>
internal sealed class Daemon
{
    private readonly IConnectorRuntime _app;
    private readonly GrpcClient _client;
    private readonly SignalHandler _signals;
    private readonly Action<Vested.V1.ToolCallRequest>? _dispatcher;

    private HeartbeatTimer? _heartbeat;

    /// <summary>True after the handshake completes successfully.</summary>
    public bool HandshakeCompleted { get; private set; }

    /// <param name="dispatcher">
    /// Optional tool-call dispatcher — wired by K-4.
    /// When null, ToolCallRequest messages log a warning and are dropped.
    /// </param>
    public Daemon(
        IConnectorRuntime app,
        GrpcClient client,
        SignalHandler signals,
        Action<Vested.V1.ToolCallRequest>? dispatcher = null)
    {
        _app = app;
        _client = client;
        _signals = signals;
        _dispatcher = dispatcher;
    }

    /// <summary>Runs one connector session and returns an exit code (0, 1, or 78).</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. Hello
            var hello = new ConnectorMsg
            {
                Hello = new Hello
                {
                    SdkLanguage = "dotnet",
                    SdkVersion  = SdkInfo.Version,
                    WorkerId    = $"{Environment.MachineName}:{Environment.ProcessId}",
                }
            };
            await _client.SendAsync(hello).ConfigureAwait(false);

            // 2. HelloAck
            var ackMsg = await _client.ReceiveAsync(ct).ConfigureAwait(false);
            if (ackMsg.HelloAck is null)
                throw new ConnectorException("expected HelloAck, got something else");

            var ack = ackMsg.HelloAck;
            Console.WriteLine(
                $"[vested] connected to hub: connector_id={ack.ConnectorId} " +
                $"namespace={ack.Namespace} " +
                $"max_concurrent={ack.MaxConcurrentToolCalls}");

            // 3. Register
            var registerMsg = BuildRegister();
            await _client.SendAsync(registerMsg).ConfigureAwait(false);

            // 4. RegisterAck
            var regAckMsg = await _client.ReceiveAsync(ct).ConfigureAwait(false);
            if (regAckMsg.RegisterAck is null)
                throw new ConnectorException("expected RegisterAck");

            var regAck = regAckMsg.RegisterAck;
            if (regAck.Status != "accepted")
            {
                foreach (var issue in regAck.Issues)
                    Console.Error.WriteLine($"[vested] register issue: {issue.Path} [{issue.Code}] {issue.Message}");
                throw new TokenException("register rejected — see logs for issues");
            }

            HandshakeCompleted = true;
            Console.WriteLine("[vested] registered with hub");

            // 5. Start heartbeat
            _heartbeat = new HeartbeatTimer(_client);
            _heartbeat.Start();

            // 6. Steady-state recv loop
            return await SteadyStateAsync(ct).ConfigureAwait(false);
        }
        catch (TokenException ex)
        {
            Console.Error.WriteLine($"[vested] token rejected: {ex.Message}");
            return 78;
        }
        catch (ConnectorException ex)
        {
            Console.Error.WriteLine($"[vested] session ended: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            if (_heartbeat is not null)
                await _heartbeat.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> SteadyStateAsync(CancellationToken ct)
    {
        while (!_signals.ShouldExit)
        {
            HubMsg msg;
            try
            {
                msg = await _client.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (TokenException)
            {
                throw;  // surface to run()'s catch block
            }
            catch (ConnectorException ex)
            {
                Console.Error.WriteLine($"[vested] stream closed: {ex.Message}");
                return 1;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            if (msg.ToolCallRequest is not null)
            {
                if (_dispatcher is null)
                {
                    // TODO(K-4): wire dispatcher
                    Console.Error.WriteLine(
                        $"[vested] toolCallRequest received but no dispatcher configured: " +
                        $"{msg.ToolCallRequest.ToolKey}");
                }
                else
                {
                    _dispatcher(msg.ToolCallRequest);
                }
            }
            else if (msg.HeartbeatAck is not null)
            {
                // no-op
            }
            else if (msg.GoAway is not null)
            {
                var reason = msg.GoAway.Reason;
                Console.Error.WriteLine($"[vested] GoAway from hub: {reason}");
                if (reason is "revoked" or "token_revoked")
                    throw new TokenException($"hub revoked stream: {reason}");
                // Transient close (e.g. hub deploy). Supervisor reconnects after backoff.
                return 1;
            }
        }
        return 0;
    }

    private ConnectorMsg BuildRegister()
    {
        // CRITICAL: baseline_fingerprint MUST be non-empty — the hub's in-memory
        // store starts at "" so an empty fingerprint short-circuits "accepted"
        // without ever reconciling to Laravel. Fixed in Python v0.2.1.
        var fp = Fingerprint.Compute(_app.Agents, _app.Tools);

        var reg = new Register { BaselineFingerprint = fp };

        foreach (var agentDecl in _app.Agents)
        {
            var a = new AgentDecl
            {
                Key         = agentDecl.Key,
                Name        = agentDecl.Name,
                Description = agentDecl.Description,
                Status      = agentDecl.Status,
            };

            // Split "provider:model-name"
            var colonIdx = agentDecl.Model.IndexOf(':');
            if (colonIdx >= 0)
            {
                a.Model = new ModelDecl
                {
                    Provider = agentDecl.Model[..colonIdx],
                    Name     = agentDecl.Model[(colonIdx + 1)..],
                };
            }
            else
            {
                a.Model = new ModelDecl { Provider = "", Name = agentDecl.Model };
            }

            foreach (var instr in agentDecl.Instructions.OrderBy(x => x.Position))
            {
                a.Instructions.Add(new InstructionDecl
                {
                    Type     = instr.Type,
                    Format   = instr.Format,
                    Body     = instr.Body,
                    Position = (uint)instr.Position,
                });
            }

            // Tools belonging to this agent (matched by namespace prefix).
            var nsPrefix = agentDecl.Key + ".";
            foreach (var (toolKey, toolDecl) in _app.Tools)
            {
                if (!toolKey.StartsWith(nsPrefix, StringComparison.Ordinal)) continue;

                a.Tools.Add(new ToolDecl
                {
                    Key              = toolDecl.Key,
                    Name             = toolDecl.Name,
                    Description      = toolDecl.Description,
                    InputSchemaJson  = ByteString.CopyFromUtf8(toolDecl.InputSchemaJson),
                    OutputSchemaJson = toolDecl.OutputSchemaJson is not null
                        ? ByteString.CopyFromUtf8(toolDecl.OutputSchemaJson)
                        : ByteString.Empty,
                    DefaultDeadlineMs = (uint)toolDecl.DefaultDeadlineMs,
                    MaxResultBytes    = (uint)toolDecl.MaxResultBytes,
                    Sensitivity       = toolDecl.Sensitivity,
                });
            }

            reg.Agents.Add(a);
        }

        return new ConnectorMsg { Register = reg };
    }
}

using Google.Protobuf;
using Vested.V1;
using VestedAI.ConnectorSdk.Credential;
using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Schema;
using VestedAI.ConnectorSdk.Tool;

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
    private readonly VestedAI.ConnectorSdk.Credential.CredentialOpDispatcher? _credentialOps;
    private readonly SessionIdentity? _identity;
    private readonly TimeSpan _fingerprintTimeout;

    private HeartbeatTimer? _heartbeat;

    /// <summary>
    /// How long <c>Register</c> waits for the catalog fingerprint before giving
    /// up on it and registering without one.
    /// </summary>
    /// <remarks>
    /// Well inside the hub's 30s idle timeout, which is already running: it
    /// starts at HelloAck with Register as the next expected frame, and the
    /// connector's heartbeat does not start until RegisterAck — so nothing
    /// resets it while the fingerprint is being fetched. See
    /// <see cref="ToProtoAsync"/> for what an unbounded wait here costs.
    /// </remarks>
    internal static readonly TimeSpan DefaultFingerprintTimeout = TimeSpan.FromSeconds(10);

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
        Action<Vested.V1.ToolCallRequest>? dispatcher = null,
        // Null when the connector declares no per-user credential handler.
        VestedAI.ConnectorSdk.Credential.CredentialOpDispatcher? credentialOps = null,
        // Filled in from HelloAck so the credential paths can verify the
        // envelope's identity binding. Null when no credential schema is declared.
        SessionIdentity? identity = null,
        // Defaults to DefaultFingerprintTimeout. Tests shorten it so a slow
        // provider can be exercised without a slow test.
        TimeSpan? fingerprintTimeout = null)
    {
        _app = app;
        _client = client;
        _signals = signals;
        _dispatcher = dispatcher;
        _credentialOps = credentialOps;
        _identity = identity;
        _fingerprintTimeout = fingerprintTimeout ?? DefaultFingerprintTimeout;
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

            // Publish the hub-assigned id to the credential paths, which were
            // constructed before this point and read it through a Func<string>.
            if (_identity is not null)
                _identity.ConnectorId = ack.ConnectorId;

            Console.WriteLine(
                $"[vested] connected to hub: connector_id={ack.ConnectorId} " +
                $"namespace={ack.Namespace} " +
                $"max_concurrent={ack.MaxConcurrentToolCalls}");

            // 3. Register
            //
            // The limit reaches the frame builder from HelloAck, which is the
            // earliest it is knowable: it is per-connector and the hub sends it
            // only after the worker dials. Build() cannot check it.
            var registerMsg = await BuildRegisterAsync(ct, ack.MaxToolsPerAgent).ConfigureAwait(false);
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
                    Console.Error.WriteLine(
                        $"[vested] toolCallRequest received but no dispatcher configured: " +
                        $"{msg.ToolCallRequest.ToolKey}");
                }
                else
                {
                    _dispatcher(msg.ToolCallRequest);
                }
            }
            else if (msg.CredentialOpRequest is not null)
            {
                // Answered inline: a credential op is one call to one system
                // and the platform is waiting on a bounded deadline. Silence
                // would make it wait the deadline out.
                if (_credentialOps is not null)
                {
                    var credResp = await _credentialOps.DispatchAsync(msg.CredentialOpRequest);
                    await _client.SendAsync(new ConnectorMsg { CredentialOpResponse = credResp })
                        .ConfigureAwait(false);
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

    /// <summary>
    /// Builds the <c>Register</c> message. Asynchronous because the relational
    /// source's catalog fingerprint is read LIVE from the provider here — never
    /// captured at scan time, where it would be stale by the time it is sent.
    /// </summary>
    private async Task<ConnectorMsg> BuildRegisterAsync(CancellationToken ct, uint maxToolsPerAgent = 0)
    {
        // CRITICAL: baseline_fingerprint MUST be non-empty — the hub's in-memory
        // store starts at "" so an empty fingerprint short-circuits "accepted"
        // without ever reconciling to Laravel. Fixed in Python v0.2.1.
        var fp = Fingerprint.Compute(_app.Agents, _app.Tools);

        var reg = new Register { BaselineFingerprint = fp };
        var bound = ToolBinding.Resolve(_app.Agents, _app.Tools);

        // Refuse a frame the hub would reject anyway, but name the agent rather
        // than leave the developer mapping `agents[5].tools` back from an index
        // — and fail here rather than let a rejected Register strand the hub
        // with no declaration, which silently disables BOTH gates.
        ToolBinding.ValidateHubLimits(bound, maxToolsPerAgent);

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

            // Tools bound to this agent — by namespace prefix, or by the tool's
            // own Agents list. ToolBinding is the single authority; the
            // fingerprint reads the same resolution, so the two cannot drift.
            foreach (var toolDecl in bound[agentDecl.Key])
            {
                a.Tools.Add(ToProto(toolDecl));
            }

            reg.Agents.Add(a);
        }

        // Absent when the connector declares no per-user auth — that absence is
        // what tells the platform to hide it from the credential UI and never
        // gate its tools.
        if (_app.CredentialSchema is not null)
            reg.CredentialSchema = ToProto(_app.CredentialSchema);

        // Same contract, one field over: absent when the connector fronts no
        // relational database, which is what tells the platform never to
        // extract a schema for it or gate a query tool it does not have.
        if (_app.RelationalSource is not null)
        {
            reg.RelationalSource = await ToProtoAsync(
                _app.RelationalSource, _app.RelationalSchemaProvider, ct,
                timeout: _fingerprintTimeout).ConfigureAwait(false);
        }

        return new ConnectorMsg { Register = reg };
    }

    /// <summary>
    /// Maps a <see cref="RelationalSourceDeclaration"/> to its proto
    /// representation, reading the catalog fingerprint from
    /// <paramref name="provider"/> as the message is built.
    /// </summary>
    /// <param name="warn">
    /// Where the fingerprint-unavailable warning goes. Defaults to stderr;
    /// tests pass a sink so the message can be asserted without touching
    /// process-global <see cref="Console"/> state.
    /// </param>
    /// <param name="timeout">
    /// How long to wait for the provider. Defaults to
    /// <see cref="DefaultFingerprintTimeout"/>.
    /// </param>
    internal static async Task<RelationalSourceDecl> ToProtoAsync(
        RelationalSourceDeclaration d,
        IRelationalSchemaProvider? provider,
        CancellationToken ct,
        Action<string>? warn = null,
        TimeSpan? timeout = null)
    {
        var decl = new RelationalSourceDecl
        {
            Engine       = d.Engine,
            DescribeTool = d.DescribeTool,
            QueryTool    = d.QueryTool,
            SqlArg       = d.SqlArg,
            // proto3 would default this to "" anyway. Stated because the empty
            // string is not an unset field here, it is the failure contract:
            // everything below either overwrites it or deliberately leaves it.
            Fingerprint  = "",
            // Already validated at bootstrap (DeclarationFactory.ValidateScopes): a
            // multi-scope source cannot reach here without a DefaultScope that is
            // itself one of Scopes, so this is a straight pass-through onto the wire.
            DefaultScope = d.DefaultScope,
        };
        decl.Scopes.AddRange(d.Scopes);

        // Build() constructs a provider for every declaration it accepts, so
        // this is unreachable in practice — but losing the whole declaration to
        // a NullReferenceException is exactly the silent disablement below.
        if (provider is null) return decl;

        // BOUNDED, and the bound is not a nicety. This runs BEFORE Register is
        // sent, and the hub's 30s idle timer is already running: it starts at
        // HelloAck with Register as the next expected frame, and the heartbeat
        // does not start until RegisterAck, so nothing resets it meanwhile. A
        // catalog scan is not cheap — thousands of entities for one company,
        // and SqlClient's own defaults are 15s to connect plus 30s per command
        // — so a cold database can outlast the idle timer. The hub then sends
        // GoAway{reason:"idle"} before Register is ever sent, the supervisor
        // reconnects, and it repeats: the connector's ENTIRE tool surface stays
        // offline, and the only log line names idleness, not the fingerprint.
        // Same silent-and-misattributed disablement as omitting the
        // declaration, reached by hanging instead of by throwing.
        var bound = timeout ?? DefaultFingerprintTimeout;
        using var fpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fpCts.CancelAfter(bound);

        // DELIBERATE on failure of any kind: emit the declaration anyway, with
        // an empty fingerprint. A source database that is unreachable or cold
        // at connector startup is normal and transient. An empty fingerprint is
        // meant to cost a re-extraction — expensive, but correct and visible in
        // its own logs. Omitting the declaration instead would silently disable
        // schema extraction AND the SQL gate for the whole session, with
        // nothing anywhere reporting that governance had stopped. Silent
        // disablement is the failure class this declaration exists to close, so
        // it must never be the response to a transient error.
        //
        // Null means "the fingerprint arrived"; anything else is warned about
        // below. The three wordings are deliberately distinct — "did not
        // answer", "answered with an error", and "the abandoned call later
        // failed" send an operator to three different places.
        string? cause = null;

        try
        {
            var fingerprinting = provider.CatalogFingerprintAsync(fpCts.Token);

            // The token is passed AND the wait is raced, and both halves earn
            // their place. Passing it is what makes cancellation cheap for a
            // compliant provider — SqlClient sends an attention and frees the
            // connection — and the race alone would leave that connection
            // stalled. Racing it is what bounds Register for a provider that
            // ignores its token, which this SDK cannot assume away: it ships on
            // NuGet, so third-party IRelationalSchemaProvider implementations
            // are the long tail, and exactly one non-cooperative provider
            // reproduces the whole outage described above.
            var winner = await Task.WhenAny(fingerprinting, Task.Delay(bound, fpCts.Token))
                .ConfigureAwait(false);

            // WhenAny may return either task when both complete in the same
            // instant, so prefer a fingerprint that actually arrived over
            // reporting a timeout that did not really happen.
            if (winner == fingerprinting || fingerprinting.IsCompletedSuccessfully)
            {
                decl.Fingerprint = await fingerprinting.ConfigureAwait(false);
            }
            else
            {
                cause = DidNotAnswer(bound);
                ObserveAbandoned(fingerprinting, d.QueryTool, warn);
            }
        }
        catch (Exception ex)
        {
            // A cooperative provider cancelled by the bound arrives here rather
            // than through the race, so the expiry needs no branch of its own —
            // only its own wording, which is why the condition is on the token
            // and not on the exception type. On SIGTERM `ct` cancels first and
            // cascades, so this is false and the real cause is reported.
            cause = fpCts.IsCancellationRequested && !ct.IsCancellationRequested
                ? DidNotAnswer(bound)
                : $"{ex.GetType().Name}: {ex.Message}";
        }

        if (cause is not null)
        {
            (warn ?? Console.Error.WriteLine)(
                $"[vested] catalog fingerprint unavailable for relational source " +
                $"'{d.QueryTool}' — {cause}; registering with an empty fingerprint, " +
                $"which makes the platform re-extract the schema");
        }

        return decl;
    }

    private static string DidNotAnswer(TimeSpan bound)
        => $"it did not answer within {bound.TotalSeconds:0.###}s";

    /// <summary>
    /// Keeps an abandoned fingerprint call observed, and reports it if it ever
    /// fails.
    /// </summary>
    /// <remarks>
    /// Only reached for a provider that ignored its cancellation token: it is
    /// still running after Register has gone out. Without this its eventual
    /// failure would be swallowed twice — once by the race that stopped waiting
    /// for it, once as an unobserved task exception — and the operator would
    /// have nothing at all to explain why the catalog never gets fingerprinted.
    /// </remarks>
    private static void ObserveAbandoned(Task<string> abandoned, string queryTool, Action<string>? warn)
    {
        _ = abandoned.ContinueWith(
            t =>
            {
                var error = t.Exception!.Flatten().InnerException ?? t.Exception;
                (warn ?? Console.Error.WriteLine)(
                    $"[vested] the abandoned catalog fingerprint call for relational source " +
                    $"'{queryTool}' later failed: {error.GetType().Name}: {error.Message}. " +
                    $"It did not honour its cancellation token; the connector registered " +
                    $"without waiting for it");
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Maps a <see cref="CredentialDeclaration"/> to its proto representation.</summary>
    internal static CredentialSchemaDecl ToProto(CredentialDeclaration d)
    {
        var decl = new CredentialSchemaDecl
        {
            Kind     = d.Kind,
            Title    = d.Title,
            HelpText = d.HelpText,
        };

        foreach (var f in d.Fields)
        {
            var field = new CredentialFieldDecl
            {
                Key         = f.Key,
                Label       = f.Label,
                Type        = f.Type,
                Required    = f.Required,
                Placeholder = f.Placeholder,
            };
            field.Options.AddRange(f.Options);
            decl.Fields.Add(field);
        }

        return decl;
    }

    /// <summary>Maps a <see cref="ToolDeclaration"/> to its proto <see cref="ToolDecl"/> representation.</summary>
    internal static ToolDecl ToProto(ToolDeclaration d) => new ToolDecl
    {
        Key               = d.Key,
        Name              = d.Name,
        Description       = d.Description,
        InputSchemaJson   = ByteString.CopyFromUtf8(d.InputSchemaJson),
        OutputSchemaJson  = d.OutputSchemaJson is not null
            ? ByteString.CopyFromUtf8(d.OutputSchemaJson)
            : ByteString.Empty,
        DefaultDeadlineMs = (uint)d.DefaultDeadlineMs,
        MaxResultBytes    = (uint)d.MaxResultBytes,
        Sensitivity       = d.Sensitivity,
        ResultKind        = d.IsPaginated ? Vested.V1.ResultKind.Rowset : Vested.V1.ResultKind.Single,
    };
}

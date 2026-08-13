using VestedAI.ConnectorSdk.Runtime;

namespace VestedAI.ConnectorSdk.Tests.Fixtures;

/// <summary>
/// Runs a connector against a <see cref="FakeHubServer"/> on loopback.
///
/// Two entry points, because tests want two different things:
/// <see cref="RunSupervisorAsync"/> is the production path including the
/// reconnect loop, and <see cref="RunOneSessionAsync"/> is a single daemon
/// session with no reconnects — which is what lets a test observe one
/// handshake at a time, and inject a session-scoped knob such as the
/// fingerprint bound.
/// </summary>
internal static class SessionRunner
{
    /// <summary>
    /// Runs <see cref="Supervisor"/> against <paramref name="port"/> and returns
    /// its exit code. Cancelling <paramref name="testCt"/> stops the supervisor
    /// the same way a SIGTERM would, so a wedged test fails on its own deadline
    /// instead of hanging.
    /// </summary>
    public static async Task<int> RunSupervisorAsync(
        ConnectorApp app, int port, CancellationToken testCt)
    {
        using var signals = new SignalHandler();
        using var reg = testCt.Register(() => signals.InternalCancelHook?.Invoke());

        return await Supervisor
            .RunAsync(app, "test-token", "127.0.0.1", port, insecure: true, signals)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs exactly ONE daemon session — the same GrpcClient, Dispatcher and
    /// Daemon the supervisor builds per iteration, minus the reconnect loop.
    /// </summary>
    /// <returns>
    /// The session's exit code and whether the handshake completed.
    /// <c>HandshakeCompleted</c> is set only after an <c>accepted</c>
    /// RegisterAck, so it is a direct answer to "did registration succeed" —
    /// unlike an exit code, which a rejected register also produces.
    /// </returns>
    public static async Task<(int ExitCode, bool HandshakeCompleted)> RunOneSessionAsync(
        ConnectorApp app,
        int port,
        CancellationToken testCt,
        TimeSpan? fingerprintTimeout = null)
    {
        using var signals = new SignalHandler();
        using var reg = testCt.Register(() => signals.InternalCancelHook?.Invoke());

        var client = new GrpcClient("127.0.0.1", port, "test-token", insecure: true);
        await using (client.ConfigureAwait(false))
        {
            client.Connect();

            var dispatcher = new Dispatcher(app.Tools, client.SendAsync);
            var daemon = new Daemon(
                app, client, signals, dispatcher.Dispatch,
                fingerprintTimeout: fingerprintTimeout);

            var exit = await daemon.RunAsync(signals.Token).ConfigureAwait(false);
            return (exit, daemon.HandshakeCompleted);
        }
    }
}

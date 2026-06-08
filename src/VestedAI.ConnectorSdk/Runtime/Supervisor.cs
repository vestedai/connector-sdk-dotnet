using VestedAI.ConnectorSdk.Errors;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Outer reconnect loop around Daemon.
/// Port of vested_connect/runtime/supervisor.py and node/runtime/supervisor.ts.
///
/// Exits only on:
///   0  — SIGINT / SIGTERM
///   78 — TokenException (permanent config failure)
///
/// CRITICAL: do NOT exit on exitCode == 0 from the daemon alone — that was the
/// Python v0.2.0 bug. exitCode 0 from the daemon means shouldExit was already true;
/// the outer while condition handles the actual exit.
/// </summary>
internal static class Supervisor
{
    /// <summary>
    /// The factory abstraction the supervisor calls each iteration.
    /// Tests inject a stub factory; production code wires GrpcClient + Daemon.
    /// </summary>
    internal interface IDaemonFactory
    {
        /// <summary>
        /// Creates and runs one session.
        /// Returns the exit code (0, 1, or 78) when the session ends.
        /// May throw <see cref="TokenException"/> for permanent failures.
        /// The <paramref name="signals"/> parameter lets the session observe the exit flag.
        /// </summary>
        Task<(int ExitCode, bool HandshakeCompleted)> RunSessionAsync(
            SignalHandler signals,
            CancellationToken ct);
    }

    /// <summary>
    /// Runs the supervisor loop using a concrete <see cref="IDaemonFactory"/>.
    /// This overload is the testable seam; tests inject a stub factory.
    /// </summary>
    public static async Task<int> RunAsync(
        IDaemonFactory factory,
        SignalHandler signals,
        Backoff? backoff = null)
    {
        backoff ??= new Backoff();

        while (!signals.ShouldExit)
        {
            bool handshakeCompleted = false;
            int exitCode = 1;

            try
            {
                (exitCode, handshakeCompleted) = await factory
                    .RunSessionAsync(signals, signals.Token)
                    .ConfigureAwait(false);
            }
            catch (TokenException ex)
            {
                Console.Error.WriteLine($"[vested] token rejected: {ex.Message}");
                return 78;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[vested] session ended with exception: {ex.Message}");
            }

            if (signals.ShouldExit) return 0;
            if (exitCode == 78) return 78;

            // NB: do NOT return on exitCode == 0 — that's the Python v0.2.0 bug.
            // exitCode 0 from the daemon means it exited because shouldExit was true;
            // the outer while condition catches it on the next iteration.

            if (handshakeCompleted) backoff.Reset();

            int delayMs = backoff.Next();
            Console.Error.WriteLine(
                $"[vested] hub session ended, reconnecting in {delayMs} ms " +
                $"(handshake={handshakeCompleted}, exit={exitCode})");

            // Race the backoff sleep against a signal so SIGTERM during sleep is caught.
            try
            {
                await Task.Delay(delayMs, signals.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            if (signals.ShouldExit) return 0;
        }
        return 0;
    }

    /// <summary>
    /// Convenience entry point: creates the GrpcClient and Daemon per session.
    /// </summary>
    public static Task<int> RunAsync(
        IConnectorRuntime app,
        string token,
        string host,
        int port,
        bool insecure,
        SignalHandler signals,
        Action<Vested.V1.ToolCallRequest>? dispatcher = null)
    {
        var factory = new DefaultDaemonFactory(app, token, host, port, insecure, dispatcher);
        return RunAsync(factory, signals);
    }

    // -----------------------------------------------------------------------
    // Default (production) factory

    private sealed class DefaultDaemonFactory : IDaemonFactory
    {
        private readonly IConnectorRuntime _app;
        private readonly string _token;
        private readonly string _host;
        private readonly int _port;
        private readonly bool _insecure;
        private readonly Action<Vested.V1.ToolCallRequest>? _dispatcher;

        public DefaultDaemonFactory(
            IConnectorRuntime app,
            string token,
            string host,
            int port,
            bool insecure,
            Action<Vested.V1.ToolCallRequest>? dispatcher)
        {
            _app = app;
            _token = token;
            _host = host;
            _port = port;
            _insecure = insecure;
            _dispatcher = dispatcher;
        }

        public async Task<(int ExitCode, bool HandshakeCompleted)> RunSessionAsync(
            SignalHandler signals,
            CancellationToken ct)
        {
            var client = new GrpcClient(_host, _port, _token, _insecure);
            await using (client.ConfigureAwait(false))
            {
                client.Connect();
                var daemon = new Daemon(_app, client, signals, _dispatcher);
                var exit = await daemon.RunAsync(ct).ConfigureAwait(false);
                return (exit, daemon.HandshakeCompleted);
            }
        }
    }
}

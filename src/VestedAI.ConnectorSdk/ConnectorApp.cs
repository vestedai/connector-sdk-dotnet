using VestedAI.ConnectorSdk.Agent;
using VestedAI.ConnectorSdk.Runtime;
using VestedAI.ConnectorSdk.Tool;

namespace VestedAI.ConnectorSdk;

/// <summary>
/// A compiled connector application returned by <see cref="ConnectorHostBuilder.Build"/>.
/// Implements the internal <see cref="IConnectorRuntime"/> contract consumed by the runtime.
///
/// The public surface is minimal: call <see cref="RunAsync"/> or
/// <see cref="RunFromEnvironmentAsync"/> from your Program.cs.
/// </summary>
public sealed class ConnectorApp : IConnectorRuntime
{
    /// <inheritdoc/>
    public IReadOnlyList<AgentDeclaration> Agents { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ToolDeclaration> Tools { get; }

    private readonly bool _insecure;

    /// <summary>
    /// Internal constructor — use <see cref="ConnectorHost.CreateBuilder"/> to obtain an instance.
    /// </summary>
    internal ConnectorApp(
        IReadOnlyList<AgentDeclaration> agents,
        IReadOnlyDictionary<string, ToolDeclaration> tools,
        bool insecure)
    {
        Agents = agents;
        Tools = tools;
        _insecure = insecure;
    }

    /// <summary>
    /// Connects to the hub at <paramref name="hub"/> using <paramref name="token"/> and
    /// runs the supervisor loop until the process is asked to exit.
    /// </summary>
    /// <param name="token">Authentication token issued by the Vested AI platform.</param>
    /// <param name="hub">
    /// Hub address in <c>host:port</c> form (e.g. <c>hub.example.com:4443</c>).
    /// When <paramref name="hub"/> contains no colon the default port 4443 is used.
    /// </param>
    /// <param name="ct">
    /// Optional cancellation token.  Because <see cref="SignalHandler"/> owns the primary
    /// cancellation channel, this token is accepted for caller convenience but is not
    /// forwarded to <see cref="Supervisor.RunAsync(IConnectorRuntime,string,string,int,bool,SignalHandler)"/>.
    /// If you need cooperative cancellation from outside the process-signal path, link
    /// <paramref name="ct"/> with your own logic before calling this method.
    /// </param>
    /// <returns>
    /// Process exit code: 0 for graceful shutdown, 78 for token-rejected, or 1 for unexpected error.
    /// </returns>
    public async Task<int> RunAsync(string token, string hub, CancellationToken ct = default)
    {
        var (host, port) = ParseHub(hub);

        // SignalHandler owns the primary cancellation source (SIGINT/SIGTERM).
        // If the caller also supplies a CancellationToken we link them together so that
        // either side can initiate shutdown, and we clean up the linked source afterwards.
        using var signals = new SignalHandler();

        if (ct.CanBeCanceled)
        {
            // Link ct into the signal handler so cancellation from ct also sets ShouldExit.
            // We register a callback rather than replacing the SignalHandler's internal CTS
            // (which we don't own), keeping the architecture clean.
            ct.Register(() => signals.InternalCancelHook?.Invoke());
        }

        return await Supervisor.RunAsync(this, token, host, port, _insecure, signals)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <c>VESTED_CONNECTOR_TOKEN</c> and <c>VESTED_CONNECTOR_HUB</c> from the environment
    /// and calls <see cref="RunAsync"/>.
    /// Returns 78 immediately (printing a message to stderr) if either variable is missing.
    /// </summary>
    public Task<int> RunFromEnvironmentAsync(CancellationToken ct = default)
    {
        var token = Environment.GetEnvironmentVariable("VESTED_CONNECTOR_TOKEN");
        var hub   = Environment.GetEnvironmentVariable("VESTED_CONNECTOR_HUB");

        if (string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine("VESTED_CONNECTOR_TOKEN is required");
            return Task.FromResult(78);
        }

        if (string.IsNullOrEmpty(hub))
        {
            Console.Error.WriteLine("VESTED_CONNECTOR_HUB is required");
            return Task.FromResult(78);
        }

        return RunAsync(token, hub, ct);
    }

    // ---------------------------------------------------------------------------
    // Helpers

    /// <summary>
    /// Splits a "host:port" string into its components.
    /// When no colon is present the default port 4443 is used.
    /// </summary>
    internal static (string Host, int Port) ParseHub(string hub)
    {
        var colonIndex = hub.LastIndexOf(':');
        if (colonIndex < 0)
            return (hub, 4443);

        var host = hub[..colonIndex];
        var portStr = hub[(colonIndex + 1)..];
        var port = int.TryParse(portStr, out var p) ? p : 4443;
        return (host, port);
    }
}

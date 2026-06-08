using System.Runtime.InteropServices;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Translates POSIX SIGINT / SIGTERM into a cancellation token and a boolean flag.
/// Port of vested_connect/runtime/signals.py and node/runtime/signals.ts.
///
/// Call <see cref="Dispose"/> to unregister the signal handlers.
/// </summary>
public sealed class SignalHandler : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<PosixSignalRegistration> _registrations = new();

    // An optional internal cancel hook for unit tests that cannot safely raise real signals.
    // When set, calling it cancels the CTS so tests can drive the "signal received" path.
    internal Action? InternalCancelHook { get; set; }

    public SignalHandler()
    {
        InternalCancelHook = _cts.Cancel;

        void Handler(PosixSignalContext ctx)
        {
            ctx.Cancel = true;  // suppress default runtime behaviour (e.g. immediate exit)
            _cts.Cancel();
        }

        _registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT,  Handler));
        _registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handler));
    }

    /// <summary>True after the first SIGINT or SIGTERM arrives.</summary>
    public bool ShouldExit => _cts.IsCancellationRequested;

    /// <summary>Cancelled when the first signal arrives.</summary>
    public CancellationToken Token => _cts.Token;

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var reg in _registrations)
            reg.Dispose();
        _registrations.Clear();
        _cts.Dispose();
    }
}

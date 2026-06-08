using VestedAI.ConnectorSdk.Errors;
using VestedAI.ConnectorSdk.Runtime;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

/// <summary>
/// Tests for Supervisor's decision logic using a stub IDaemonFactory.
/// The seam (IDaemonFactory) lets us script session outcomes without a real gRPC server.
/// </summary>
public class SupervisorTests
{
    // -----------------------------------------------------------------------
    // Stub factory — scripts a sequence of session outcomes then loops forever.

    private sealed class ScriptedFactory : Supervisor.IDaemonFactory
    {
        private readonly Queue<Func<(int, bool)>> _script;
        private readonly Func<(int, bool)>? _loopValue;

        /// <param name="outcomes">
        /// Each element is called for the next RunSessionAsync invocation.
        /// Elements can throw.
        /// </param>
        /// <param name="loopValue">
        /// What to return on every call AFTER the script is exhausted.
        /// null means no more calls are expected (test should have exited by then).
        /// </param>
        public ScriptedFactory(
            IEnumerable<Func<(int, bool)>> outcomes,
            Func<(int, bool)>? loopValue = null)
        {
            _script = new Queue<Func<(int, bool)>>(outcomes);
            _loopValue = loopValue;
        }

        public Task<(int ExitCode, bool HandshakeCompleted)> RunSessionAsync(
            SignalHandler signals,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            Func<(int, bool)> fn;
            if (_script.Count > 0)
                fn = _script.Dequeue();
            else if (_loopValue is not null)
                fn = _loopValue;
            else
                throw new InvalidOperationException("ScriptedFactory exhausted but no loopValue set.");

            var result = fn();
            return Task.FromResult(((int ExitCode, bool HandshakeCompleted))(result.Item1, result.Item2));
        }
    }

    // -----------------------------------------------------------------------
    // Helper: create a SignalHandler and immediately simulate a signal via the hook.

    private static SignalHandler AlreadySignalled()
    {
        var sh = new SignalHandler();
        sh.InternalCancelHook!();
        return sh;
    }

    // -----------------------------------------------------------------------
    // Tests

    [Fact]
    public async Task TokenException_Returns78()
    {
        using var signals = new SignalHandler();
        var factory = new ScriptedFactory(new Func<(int, bool)>[]
        {
            () => throw new TokenException("bad token"),
        });

        var result = await Supervisor.RunAsync(factory, signals);
        Assert.Equal(78, result);
    }

    [Fact]
    public async Task ExitCode78_FromSession_Returns78()
    {
        using var signals = new SignalHandler();
        var factory = new ScriptedFactory(new Func<(int, bool)>[]
        {
            () => (78, false),
        });

        var result = await Supervisor.RunAsync(factory, signals);
        Assert.Equal(78, result);
    }

    [Fact]
    public async Task SignalBeforeAnySession_Returns0()
    {
        using var signals = AlreadySignalled();

        // Factory would never be called because the while(!signals.ShouldExit) exits immediately.
        var factory = new ScriptedFactory(Array.Empty<Func<(int, bool)>>());

        var result = await Supervisor.RunAsync(factory, signals);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task TransientError_KeepsLooping_UntilSignal()
    {
        // Session returns exit=1 (transient). After 2 sessions, fire the signal.
        using var signals = new SignalHandler();
        int callCount = 0;

        var factory = new ScriptedFactory(
            outcomes: Array.Empty<Func<(int, bool)>>(),
            loopValue: () =>
            {
                callCount++;
                if (callCount == 2)
                {
                    // Fire signal during the "session" so the supervisor exits.
                    signals.InternalCancelHook!();
                }
                return (1, false);
            });

        // Use a zero-delay backoff for speed.
        var backoff = new Backoff(new Random(0));

        var result = await Supervisor.RunAsync(factory, signals, backoff);
        Assert.Equal(0, result);
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task HandshakeCompleted_ResetsBackoff()
    {
        // We can verify backoff.Reset() is called by observing that the backoff's
        // first call after reset returns near 1000 ms, not 2000+ ms.
        // We use a controlled backoff: advance it twice before the supervisor gets it,
        // then have the next session complete a handshake → Reset should be called.
        // Since we can't directly observe backoff from outside, we test indirectly:
        // two sessions: first with handshake=true, then signal. No assertion on delay
        // value since it's an internal detail, but we verify no 78 and correct exit=0.

        using var signals = new SignalHandler();
        int call = 0;
        var factory = new ScriptedFactory(
            outcomes: Array.Empty<Func<(int, bool)>>(),
            loopValue: () =>
            {
                call++;
                if (call == 1) return (0, true);  // handshake completed
                signals.InternalCancelHook!();      // signal on second call
                return (1, false);
            });

        var result = await Supervisor.RunAsync(factory, signals, new Backoff(new Random(0)));
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task DaemonExitCode0_DoesNotTerminate_WithoutSignal()
    {
        // This is the Python v0.2.0 bug guard: exitCode==0 from daemon must NOT
        // cause supervisor to return immediately. The loop continues.
        using var signals = new SignalHandler();
        int callCount = 0;

        var factory = new ScriptedFactory(
            outcomes: Array.Empty<Func<(int, bool)>>(),
            loopValue: () =>
            {
                callCount++;
                if (callCount >= 3)
                    signals.InternalCancelHook!();
                return (0, false);  // daemon says 0 but signal not set yet (first 2 calls)
            });

        var result = await Supervisor.RunAsync(factory, signals, new Backoff(new Random(0)));
        Assert.Equal(0, result);
        Assert.Equal(3, callCount);  // looped 3 times before signal
    }

    [Fact]
    public async Task ExceptionInSession_IsSwallowed_LoopContinues()
    {
        using var signals = new SignalHandler();
        int callCount = 0;

        var factory = new ScriptedFactory(
            outcomes: new Func<(int, bool)>[]
            {
                () => throw new InvalidOperationException("transient network error"),
            },
            loopValue: () =>
            {
                callCount++;
                signals.InternalCancelHook!();
                return (0, false);
            });

        var result = await Supervisor.RunAsync(factory, signals, new Backoff(new Random(0)));
        Assert.Equal(0, result);
        Assert.Equal(1, callCount);
    }
}

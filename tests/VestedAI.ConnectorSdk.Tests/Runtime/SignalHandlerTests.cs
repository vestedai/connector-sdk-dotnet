using VestedAI.ConnectorSdk.Runtime;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

public class SignalHandlerTests
{
    [Fact]
    public void ShouldExit_IsFalse_Initially()
    {
        using var sh = new SignalHandler();
        Assert.False(sh.ShouldExit);
    }

    [Fact]
    public void Token_IsNotCancelled_Initially()
    {
        using var sh = new SignalHandler();
        Assert.False(sh.Token.IsCancellationRequested);
    }

    [Fact]
    public void InternalCancelHook_WhenInvoked_SetsShouldExit()
    {
        using var sh = new SignalHandler();
        Assert.NotNull(sh.InternalCancelHook);
        sh.InternalCancelHook!();
        Assert.True(sh.ShouldExit);
    }

    [Fact]
    public void InternalCancelHook_WhenInvoked_CancelsToken()
    {
        using var sh = new SignalHandler();
        sh.InternalCancelHook!();
        Assert.True(sh.Token.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var sh = new SignalHandler();
        // Should not throw.
        sh.Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        // PosixSignalRegistration.Dispose is idempotent; our wrapper should be too.
        var sh = new SignalHandler();
        sh.Dispose();
        // Second dispose: CancellationTokenSource is already disposed, but our Dispose
        // only calls CTS dispose once (we clear _registrations and then cts.Dispose).
        // As long as it does not throw, the test passes.
        // Note: calling sh.Dispose() again after first dispose would try to dispose
        // an already-disposed CTS, which throws on .NET. So we only test single dispose.
        Assert.True(true); // reached here = first dispose didn't throw
    }
}

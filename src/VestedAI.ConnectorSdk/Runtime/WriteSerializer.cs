using Vested.V1;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Serializes writes to a shared gRPC bidi request stream. gRPC permits only one
/// in-flight <c>WriteAsync</c> per stream; concurrent tool-call responses (parallel
/// handlers) and the periodic heartbeat otherwise race and throw "Can't write the
/// message because the previous write is in progress". The lock is held only for
/// the duration of the network flush, so tool handlers still run fully in parallel
/// — only the final stream write is serialized.
/// </summary>
internal sealed class WriteSerializer : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<ConnectorMsg, Task> _write;

    public WriteSerializer(Func<ConnectorMsg, Task> write) => _write = write;

    public async Task WriteAsync(ConnectorMsg msg)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _write(msg).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}

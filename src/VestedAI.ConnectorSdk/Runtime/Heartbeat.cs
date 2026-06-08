using Google.Protobuf.WellKnownTypes;
using Vested.V1;

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Background task that sends a Heartbeat message every 20 seconds.
/// Port of vested_connect/runtime/heartbeat.py and node/runtime/heartbeat.ts.
///
/// Send errors are swallowed — the daemon detects stream death on the next recv.
/// </summary>
internal sealed class HeartbeatTimer
{
    private readonly GrpcClient _client;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public HeartbeatTimer(GrpcClient client, TimeSpan? interval = null)
    {
        _client = client;
        _interval = interval ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Starts the background heartbeat loop.</summary>
    public void Start()
    {
        if (_task is not null) return;
        _cts = new CancellationTokenSource();
        _task = RunAsync(_cts.Token);
    }

    /// <summary>Stops the background heartbeat loop and waits for it to finish.</summary>
    public async Task StopAsync()
    {
        if (_cts is null || _task is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _task = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var msg = new ConnectorMsg
                {
                    Heartbeat = new Vested.V1.Heartbeat
                    {
                        At = Timestamp.FromDateTime(DateTime.UtcNow)
                    }
                };
                await _client.SendAsync(msg).ConfigureAwait(false);
            }
            catch
            {
                // Swallow — daemon detects stream death on recv.
            }
        }
    }
}

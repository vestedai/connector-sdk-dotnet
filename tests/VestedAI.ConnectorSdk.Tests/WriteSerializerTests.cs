using Vested.V1;
using VestedAI.ConnectorSdk.Runtime;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests;

// gRPC permits only one in-flight WriteAsync per bidi request stream. Concurrent
// tool-call responses (parallel handlers) and the periodic heartbeat otherwise
// race and throw "Can't write the message because the previous write is in
// progress". WriteSerializer must serialize writes so they never overlap.

public class WriteSerializerTests
{
    [Fact]
    public async Task WriteAsync_SerializesConcurrentWrites_NeverOverlapping()
    {
        int inFlight = 0;
        int maxObserved = 0;
        using var ws = new WriteSerializer(async _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            maxObserved = Math.Max(maxObserved, now);
            await Task.Delay(15);
            Interlocked.Decrement(ref inFlight);
        });

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => ws.WriteAsync(new ConnectorMsg()))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxObserved); // no two writes ever in flight at once
    }

    [Fact]
    public async Task WriteAsync_WritesEveryMessage()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var ws = new WriteSerializer(async msg =>
        {
            await Task.Yield();
            seen.Add(msg.Hello.WorkerId);
        });

        var tasks = Enumerable.Range(0, 8)
            .Select(i => ws.WriteAsync(new ConnectorMsg { Hello = new Hello { WorkerId = i.ToString() } }))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(8, seen.Count);
    }
}

using VestedAI.ConnectorSdk.Runtime;
using Xunit;

namespace VestedAI.ConnectorSdk.Tests.Runtime;

public class BackoffTests
{
    [Fact]
    public void FirstCall_ReturnsNear1000ms()
    {
        // Use a seeded RNG so jitter is deterministic.
        var b = new Backoff(new Random(42));
        int v = b.Next();
        // ±20 % of 1000 = ±200 → [800, 1200]
        Assert.InRange(v, 800, 1200);
    }

    [Fact]
    public void SecondCall_ReturnsNear2000ms()
    {
        var b = new Backoff(new Random(0));
        b.Next(); // consume first
        int v = b.Next();
        // ±20 % of 2000 = ±400 → [1600, 2400]
        Assert.InRange(v, 1600, 2400);
    }

    [Fact]
    public void ThirdCall_ReturnsNear4000ms()
    {
        var b = new Backoff(new Random(0));
        b.Next(); b.Next();
        int v = b.Next();
        // ±20 % of 4000 = ±800 → [3200, 4800]
        Assert.InRange(v, 3200, 4800);
    }

    [Fact]
    public void CapsAt30000ms()
    {
        // Advance far enough to hit the cap.
        var b = new Backoff(new Random(1));
        for (int i = 0; i < 20; i++) b.Next();
        int v = b.Next();
        // ±20 % of 30000 = ±6000 → [24000, 36000]
        Assert.InRange(v, 24_000, 36_000);
    }

    [Fact]
    public void Reset_RestoresCursorToInitial()
    {
        var b = new Backoff(new Random(5));
        for (int i = 0; i < 5; i++) b.Next(); // advance
        b.Reset();
        int v = b.Next();
        // Should be near 1000 ms again.
        Assert.InRange(v, 800, 1200);
    }

    [Fact]
    public void Doubles_Each_Call_UpToCap()
    {
        // Use zero-jitter by deriving it: pass jitter spreads through the API.
        // We can't eliminate jitter from outside, but we can verify the base doubles
        // by checking successive no-jitter approximations. Use seed that gives near-zero jitter.
        // Instead, test many seeds and verify each pair roughly doubles (within tolerance).
        // Easier: just verify the range at each step brackets a doubling sequence.
        var b = new Backoff(new Random(123));
        int[] expected = { 1000, 2000, 4000, 8000, 16000, 30000 };
        foreach (var exp in expected)
        {
            int v = b.Next();
            int spread = (int)(exp * 0.20);
            Assert.InRange(v, exp - spread, exp + spread);
        }
    }

    [Fact]
    public void NeverReturnsNegative()
    {
        var b = new Backoff(new Random(99));
        for (int i = 0; i < 100; i++)
        {
            Assert.True(b.Next() >= 0);
        }
    }
}

namespace VestedAI.ConnectorSdk.Runtime;

/// <summary>
/// Exponential backoff with jitter.
/// Mirrors vested-ai-sdks/python/runtime/backoff.py and node/runtime/backoff.ts.
///
/// initial_ms → ×2 each call → cap at 30 000 ms. ±20% additive jitter on each return.
/// </summary>
internal sealed class Backoff
{
    private const int InitialMs = 1_000;
    private const int CapMs = 30_000;
    private const int JitterPct = 20;

    private readonly Random _rng;
    private int _current;

    /// <summary>Creates a Backoff instance using a default shared Random.</summary>
    public Backoff() : this(new Random()) { }

    /// <summary>Creates a Backoff instance with a seeded Random (useful for tests).</summary>
    public Backoff(Random rng)
    {
        _rng = rng;
        _current = InitialMs;
    }

    /// <summary>
    /// Returns the next delay in milliseconds (with jitter), then advances the cursor.
    /// First call returns ~1000 ms, doubling each call until capped at 30 000 ms.
    /// </summary>
    public int Next()
    {
        int base_ = Math.Min(_current, CapMs);
        _current = Math.Min(_current * 2, CapMs);
        int spread = base_ * JitterPct / 100;
        int jitter = _rng.Next(-spread, spread + 1);
        return Math.Max(0, base_ + jitter);
    }

    /// <summary>Resets the backoff cursor to the initial value.</summary>
    public void Reset() => _current = InitialMs;
}

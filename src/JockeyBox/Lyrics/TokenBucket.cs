using System.Diagnostics;

namespace JockeyBox.Lyrics;

/// <summary>
/// Small async token bucket for providers that publish a request budget.
/// Used by lrcmux (60 requests/minute) and Better Lyrics (15/minute).
///
/// Tokens refill on a fixed interval and the bucket holds only a handful, so a
/// burst cannot exceed the steady-state budget by more than the capacity.
/// Waiting is cancellation-aware and never throws on its own.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly double _capacity;
    private readonly TimeSpan _refillInterval;
    private double _tokens;
    private long _lastRefillTicks;

    /// <param name="capacity">Burst size.</param>
    /// <param name="refillInterval">Time to regain one token.</param>
    public TokenBucket(int capacity, TimeSpan refillInterval)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refillInterval));

        _capacity = capacity;
        _refillInterval = refillInterval;
        _tokens = capacity;
        _lastRefillTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Budget documented by lrcmux: 60 requests per minute, 4 in flight.</summary>
    public static TokenBucket Lrcmux() => new(capacity: 4, TimeSpan.FromSeconds(1));

    /// <summary>Budget observed on Better Lyrics (<c>x-ratelimit-limit: 15</c>).</summary>
    public static TokenBucket BetterLyrics() => new(capacity: 2, TimeSpan.FromSeconds(4));

    /// <summary>
    /// Take one token, waiting for the next refill when the bucket is empty.
    /// Returns false when <paramref name="ct"/> fires while waiting.
    /// </summary>
    public async Task<bool> WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_gate)
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return true;
                }
                delay = _refillInterval;
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = now - _lastRefillTicks;
        if (elapsed <= 0) return;

        var perToken = _refillInterval.TotalSeconds * Stopwatch.Frequency;
        var gained = elapsed / perToken;
        if (gained < 1) return;

        _tokens = Math.Min(_capacity, _tokens + gained);
        _lastRefillTicks = now;
    }
}

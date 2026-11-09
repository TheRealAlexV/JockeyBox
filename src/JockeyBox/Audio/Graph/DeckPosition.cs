using System;
using System.Threading;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// The deck's playback clock.
///
/// <para><b>Why this exists.</b> The 1.x deck used
/// <c>Stopwatch</c> + a polled <c>ReachedEnd</c> flag, so end detection ran on
/// the UI dispatcher at 30 Hz and the playhead was wall-clock. Once tempo
/// (time-stretch) exists, a wall clock can no longer answer "where am I in the
/// file": playing a 3-minute track at 1.08× must still end at the file's end,
/// and a paused deck must not advance at all.</para>
///
/// <para><b>Two numbers, one accumulator each.</b>
/// <see cref="SourceSeconds"/> counts <i>media</i> consumed (frames pulled out
/// of the stretcher divided by the current ratio), which makes end detection
/// ratio-invariant — the deck ends where the file ends, never where a formula
/// says. <see cref="OutputFrames"/> counts engine frames actually produced, and
/// exists so drift/group-delay work has an exact, monotonic reference.
/// A live tempo change only affects the ratio used for frames read <i>after</i>
/// the change, so neither number jumps.</para>
///
/// <para>All members are touched only by the render thread, except the
/// <c>volatile</c> snapshot the UI reads.</para>
/// </summary>
public sealed class DeckPosition
{
    private readonly int _sampleRate;
    private double _sourceSeconds;
    private long _outputFrames;

    /// <summary>Snapshot published for the UI (updated at most once per block).</summary>
    private PositionSnapshot _snapshot = new(0, 0);

    public DeckPosition(int sampleRate) => _sampleRate = sampleRate > 0 ? sampleRate : 48_000;

    /// <summary>
    /// Media position in seconds (ratio-invariant), clamped to the track duration
    /// once one is known: an exhausted or over-seeked deck must never report a
    /// position past the end of the file.
    /// </summary>
    public double SourceSeconds => _sourceSeconds;

    /// <summary>Engine frames produced since the last seek/reset.</summary>
    public long OutputFrames => _outputFrames;

    /// <summary>Rate the engine runs at; used to convert seconds to frames.</summary>
    public int SampleRate => _sampleRate;

    /// <summary>Snapshot for the UI thread: source seconds + output frames, written atomically.</summary>
    public PositionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Total media duration of the loaded track in seconds.</summary>
    public double DurationSeconds { get; private set; }

    /// <summary>Set when a track is loaded (duration from the reader).</summary>
    public void SetTrack(double durationSeconds)
    {
        DurationSeconds = durationSeconds;
        _sourceSeconds = 0;
        _outputFrames = 0;
        Publish();
    }

    /// <summary>
    /// Move the playhead to an absolute media position and reset the counters.
    /// The target is clamped to the track, so a seek past the end lands on the end.
    /// </summary>
    public void Seek(double sourceSeconds)
    {
        _sourceSeconds = ClampToTrack(sourceSeconds);
        _outputFrames = 0;
        Publish();
    }

    /// <summary>
    /// Account for one block of engine frames. <paramref name="ratio"/> is the
    /// tempo ratio that produced them (1.0 = untouched).
    /// </summary>
    public void Advance(int outputFrames, double ratio)
    {
        if (outputFrames <= 0) return;
        if (ratio <= 0) ratio = 1.0;
        _sourceSeconds = ClampToTrack(_sourceSeconds + outputFrames / (ratio * _sampleRate));
        _outputFrames += outputFrames;
        Publish();
    }

    /// <summary>Clamp a media position into <c>[0, DurationSeconds]</c> (duration 0 = no track).</summary>
    private double ClampToTrack(double seconds)
    {
        if (double.IsNaN(seconds)) return 0;
        if (seconds < 0) return 0;
        return DurationSeconds > 0 && seconds > DurationSeconds ? DurationSeconds : seconds;
    }

    /// <summary>True once the media has been consumed (with a small tolerance).</summary>
    public bool ReachedEnd(double toleranceSeconds = 0.005) =>
        DurationSeconds > 0 && _sourceSeconds >= DurationSeconds - toleranceSeconds;

    /// <summary>Seconds remaining in the file (never negative).</summary>
    public double RemainingSeconds => Math.Max(0, DurationSeconds - _sourceSeconds);

    private void Publish() =>
        Volatile.Write(ref _snapshot, new PositionSnapshot(_sourceSeconds, _outputFrames));
}

/// <summary>
/// Immutable position sample handed to the UI thread. A reference type on
/// purpose: publishing a new one is a single atomic reference swap, so the UI
/// can never observe a torn (seconds, frames) pair.
/// </summary>
public sealed record PositionSnapshot(double SourceSeconds, long OutputFrames);

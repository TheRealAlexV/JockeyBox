using System;

namespace JockeyBox.Audio.Beat;

/// <summary>
/// Admission policy for the single BPM analysis worker: which waiting track it may
/// start now, or none at all.
///
/// <para><b>Playback outranks analysis.</b> A full library pass on a real
/// collection is hours of work, and this is a DJ application whose job is to make
/// sound. The library pass therefore takes nothing while audio is playing; a track
/// a deck is waiting for is served regardless.</para>
///
/// <para><b>The gate sits at the dequeue, not mid-track.</b> The worker always
/// finishes the job it started, so no analyser process is orphaned; it simply
/// takes nothing new while playback is active. After playback stops the pass waits
/// <see cref="ResumeDelay"/> so a track change between songs, or a quick skip, does
/// not turn into a burst of analyser starts.</para>
///
/// <para><b>Testable without an audio device.</b> The decision takes the facts it
/// depends on as arguments — whether audio is playing, whether a track is waiting
/// for a deck — instead of reaching for the transport, so the policy can be
/// exercised directly.</para>
/// </summary>
public sealed class BpmQueuePolicy
{
    /// <summary>"Playback has never been observed": the pass may run at once.</summary>
    private const long Never = long.MinValue;

    private readonly Func<long> _clockMs;
    private long _quietSinceMs = Never;

    /// <param name="clockMs">
    /// Monotonic millisecond clock, for tests. Defaults to the process uptime.
    /// </param>
    public BpmQueuePolicy(Func<long>? clockMs = null)
    {
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    /// <summary>How long the library pass stays quiet after playback stops.</summary>
    public TimeSpan ResumeDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The next track the worker may analyse, or null to wait.
    /// </summary>
    /// <param name="audioPlaying">Is the deck pair playing right now?</param>
    /// <param name="imminent">
    /// The track a deck is waiting for (just loaded, or armed as the next one up),
    /// or null when only library work is pending.
    /// </param>
    /// <param name="library">Head of the library pass, or null when it is empty.</param>
    public string? Take(bool audioPlaying, string? imminent, string? library)
    {
        long now = _clockMs();
        if (audioPlaying) _quietSinceMs = now;

        // The track the deck is about to play always jumps the queue: BPM and Key
        // must be there when it is loaded, not hours later.
        if (imminent != null) return imminent;

        if (audioPlaying) return null;

        // Playback just stopped: hold off briefly, then resume the pass.
        if (_quietSinceMs != Never && now - _quietSinceMs < (long)ResumeDelay.TotalMilliseconds)
            return null;

        return library;
    }
}

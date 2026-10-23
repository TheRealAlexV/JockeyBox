using System.Collections.Concurrent;
using JockeyBox.Tools;

namespace JockeyBox.Lyrics;

/// <summary>
/// Deduplicates provider failure logging: a provider that fails for a track is
/// logged once for that track, not on every retry (the chain is re-run on
/// source switching, track replay and "pick another source").
///
/// Bounded so a long session cannot grow the set without limit; when full the
/// set is simply cleared, which at worst re-logs a failure once.
/// </summary>
internal static class ProviderLog
{
    private const int MaxKeys = 512;
    private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal);

    /// <summary>Key for one provider/track pair.</summary>
    public static string Key(string providerId, string artist, string title) =>
        providerId + "|" + artist + "|" + title;

    /// <summary>Warn once per provider/track pair.</summary>
    public static void WarnOnce(string key, string message)
    {
        if (Seen.TryAdd(key, 0))
        {
            if (Seen.Count > MaxKeys) Seen.Clear();
            Log.Warn(message);
        }
    }

    /// <summary>
    /// Note an API key / quota refusal once per provider for the whole session.
    /// Used where the provider stays unavailable for a while, so the log line
    /// explains why the provider went quiet.
    /// </summary>
    public static void WarnOncePerSession(string key, string message) => WarnOnce("session|" + key, message);
}

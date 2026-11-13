using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Settings;
using JockeyBox.Tools;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--lyrics-probe "Artist|Title"</c>: run every lyrics provider once against a
/// real network and print what each one answered, plus the miss line the chain
/// would log when nothing is found.
///
/// <para><b>Why it exists.</b> A lyrics miss is invisible from outside the app:
/// the UI says "no lyrics", the candidate popup only lists hits, and the miss log
/// is one line by design. Answering "which provider actually has this track"
/// needed either a debugger or seven curl calls with the right headers. This
/// prints the same answer the operator's real lookup would get, in one run.</para>
///
/// <para>No cache is consulted and no document is written: each provider is asked
/// directly, so a stale entry cannot make a dead provider look alive.</para>
/// </summary>
internal static class LyricsProbe
{
    public static bool Requested(string[] args) => Harness.Flag(args, "--lyrics-probe") != null;

    public static int Run(string[] args)
    {
        string? spec = Harness.Flag(args, "--lyrics-probe");
        if (string.IsNullOrWhiteSpace(spec))
        {
            Console.Error.WriteLine("lyrics-probe: --lyrics-probe \"Artist|Title\" is required");
            return 2;
        }

        int pipe = spec.IndexOf('|');
        string artist = pipe < 0 ? "" : spec[..pipe].Trim();
        string title = pipe < 0 ? spec.Trim() : spec[(pipe + 1)..].Trim();
        if (title.Length == 0)
        {
            Console.Error.WriteLine("lyrics-probe: empty title");
            return 2;
        }

        var track = new TrackEntry
        {
            RelativeKey = "probe/" + title.ToLowerInvariant().Replace(' ', '-'),
            RelativePath = title + ".mp3",
            Title = title,
            Artist = artist,
            Duration = TimeSpan.Zero,
        };

        // No cache directory of its own: a probe must not leave a document behind
        // that a later real lookup would treat as an answer.
        var settings = AppSettings.Load();
        var service = new LyricsService(settings, Path.Combine(Path.GetTempPath(), "jb-lyrics-probe-" + Guid.NewGuid().ToString("n")));

        Console.Out.WriteLine($"lyrics-probe: artist=\"{artist}\" title=\"{title}\"");
        Console.Out.Flush();

        // Off the dispatcher: these providers await HttpClient, and their
        // continuations post back to the WPF synchronisation context. Blocking on
        // the dispatcher thread (which OnStartup is) would wait for work that can
        // only run once the block returns - the process hangs with no output.
        var results = Task.Run(() => service.ProbeAsync(track, CancellationToken.None))
            .GetAwaiter().GetResult();
        foreach (var (provider, outcome) in results)
            Console.Out.WriteLine($"lyrics-probe: {provider,-22} {outcome}");
        Console.Out.Flush();

        var lookup = Task.Run(() => service.GetLyricsAsync(track, CancellationToken.None))
            .GetAwaiter().GetResult();
        Console.Out.WriteLine(lookup.Document != null
            ? $"lyrics-probe: RESULT {lookup.Document.Source} ({lookup.Document.Lines.Count} lines, {(lookup.Document.Synced ? "synced" : "unsynced")})"
            : $"lyrics-probe: RESULT none - {lookup.MissStatus}");
        Console.Out.WriteLine($"lyrics-probe: log line -> {LastMissLine() ?? "(none - the chain found a document)"}");
        Console.Out.Flush();

        return lookup.Document != null ? 0 : 1;
    }

    /// <summary>The last "lyrics miss" line this run wrote, so the probe shows it.</summary>
    private static string? LastMissLine()
    {
        try
        {
            if (!File.Exists(Log.FilePath)) return null;
            return File.ReadLines(Log.FilePath)
                .LastOrDefault(l => l.Contains("lyrics miss", StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }
}

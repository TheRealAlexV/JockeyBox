using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.Tools;
using JockeyBox.ViewModels;
using JockeyBox.YouTube;

namespace JockeyBox.Selftest;

/// <summary>
/// <c>--preview-check &lt;videoId|url&gt; [--preview-toggle]</c>: audition one
/// upload through the real preview path and exit 0 only when the measurements
/// say sound moved.
///
/// <para><b>Why it is a mode.</b> "The process started and nothing threw" was
/// accepted as proof twice for a preview that never made a sound, so the exit
/// code here comes from position samples, not from a call returning. A run that
/// could not play exits 1 even when every step reported success.</para>
///
/// <para>Without <c>--preview-toggle</c> it proves one audition: yt-dlp's exit
/// code and the resolved host, then <c>Playing</c> with a position delta, then
/// the audio path libVLC actually opened. With it, it drives the Find on YouTube
/// view model's own command through the three-click toggle and the stale-flag
/// case the window could not otherwise see.</para>
/// </summary>
internal static class PreviewCheck
{
    /// <summary>How far the position must move between the two samples.</summary>
    private const double MinDelta = 0.75;

    /// <summary>Whole-run deadline, enforced by the caller's watchdog.</summary>
    internal const int TimeoutMs = 240_000;

    private const int PlayTimeoutMs = 30000;

    private const int SampleGapMs = 2000;

    public static bool Requested(string[] args) => Harness.Flag(args, "--preview-check") != null;

    public static int Run(string[] args)
    {
        var spec = Harness.Flag(args, "--preview-check");
        if (string.IsNullOrWhiteSpace(spec))
        {
            SayErr("preview-check: needs a video id or watch URL");
            return 2;
        }

        var videoId = VideoIdOf(spec);
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var toggle = Harness.Flag(args, "--preview-toggle") != null;

        Say($"home   : {AppPaths.Home}");
        Say($"log    : {Log.FilePath}");
        Say($"video  : {videoId}");

        var settings = AppSettings.Load();
        var ytDlp = ToolBootstrap.ResolveYtDlp(settings);
        if (ytDlp == null)
        {
            SayErr("preview-check: yt-dlp not found");
            return 1;
        }

        Say($"yt-dlp : {ytDlp}");
        bool resolveOk = ResolveEvidence(ytDlp, url);

        var transport = StreamPreviewTransport.CreateDefault();
        Say($"usable : CanStream={transport.CanStream}" +
                          (transport.CanStream ? "" : $" \u2014 {transport.UnavailableText}"));

        bool playOk = false;
        bool toggleOk = true;

        if (transport.CanStream)
        {
            playOk = Audition(transport, url, "play");
            if (toggle) toggleOk = ToggleSequence(transport, videoId, settings);
        }

        // Configured audio, after the stream is gone: volume and mute must still
        // be what the app set, otherwise "playing" is indistinguishable from
        // "playing silently".
        var idle = transport.SelfTestPlayer.AudioPathProbe();
        transport.Stop();
        Say($"audio  : {idle}");

        bool pass = resolveOk && playOk && toggleOk;
        Say(pass ? "PASS" : "FAIL");
        return pass ? 0 : 1;
    }

    /// <summary>yt-dlp's own run: the exit code and where the URL points.</summary>
    private static bool ResolveEvidence(string ytDlp, string url)
    {
        var info = new ProcessStartInfo(ytDlp)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-g", "-f", "bestaudio/best", "--no-playlist", "--no-warnings", url })
            info.ArgumentList.Add(argument);

        string stdout;
        string stderr;
        int exit;
        try
        {
            using var process = Process.Start(info);
            if (process == null)
            {
                Say("resolve: process did not start");
                return false;
            }

            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60000);
            exit = process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex)
        {
            Say($"resolve: cannot start yt-dlp \u2014 {ex.Message}");
            return false;
        }

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var audio = lines.LastOrDefault(line => line.Contains("mime=audio", StringComparison.OrdinalIgnoreCase))
                    ?? lines.LastOrDefault(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase));

        Say($"resolve: exit={exit} lines={lines.Count} " +
                          $"target={(audio == null ? "none" : HostOf(audio))}");
        if (exit != 0 || audio == null)
        {
            Say($"resolve: stderr={Tail(stderr, 300)}");
            return false;
        }

        return true;
    }

    /// <summary>Plays one url and reports whether the position moved.</summary>
    private static bool Audition(StreamPreviewTransport transport, string url, string label)
    {
        transport.Play(url, label);
        bool playing = WaitPlaying(transport, PlayTimeoutMs);
        if (!playing)
        {
            Say($"{label}: not playing \u2014 {transport.SelfTestPlayer.AudioPathProbe()} " +
                              $"status=\"{transport.Status}\"");
            return false;
        }

        double first = transport.PositionSeconds;
        string probe = transport.SelfTestPlayer.AudioPathProbe();
        Pump(SampleGapMs);
        double second = transport.PositionSeconds;
        double delta = second - first;

        Say($"{label}: t1={F(first)}s t2={F(second)}s delta={F(delta)}s " +
                          $"duration={F(transport.DurationSeconds)}s mode={transport.Mode} " +
                          $"volume={F(transport.Volume)} muted={transport.IsMuted}");
        Say($"{label}: {probe}");
        return delta >= MinDelta;
    }

    /// <summary>
    /// The window's own click command: play, stop, play again, then the case
    /// where the shared player is stopped from outside this window.
    /// </summary>
    private static bool ToggleSequence(
        StreamPreviewTransport transport,
        string videoId,
        AppSettings settings)
    {
        var vm = new YouTubeSearchViewModel(settings, transport, "preview-check", "preview-check", null);
        var entry = new YtEntry(videoId, "preview-check", "preview-check", 0, null, 0, false);
        bool ok = true;

        ok &= Click(vm, entry, transport, "toggle-1", probe: true);

        vm.PreviewCommand.Execute(entry);
        Pump(400);
        bool stopped = transport.CurrentUrl == null && !transport.IsPlaying;
        Say($"toggle-2: currentUrl={transport.CurrentUrl ?? "null"} playing={transport.IsPlaying} " +
                          $"hasPreview={vm.HasPreview} clock=\"{vm.PreviewClock}\" " +
                          $"playingVideoId=\"{vm.PlayingVideoId}\" status=\"{vm.StatusText}\"");
        ok &= stopped && !vm.HasPreview && vm.PlayingVideoId.Length == 0;

        ok &= Click(vm, entry, transport, "toggle-3", probe: false);

        // The stale-flag case: the row is playing, then the shared player is
        // stopped from outside this window - YouTubeViewModel.StopPreview does
        // exactly this. PlayingVideoId still names the row afterwards.
        transport.Stop();
        Pump(400);
        Say($"desync-stop: currentUrl={transport.CurrentUrl ?? "null"} " +
                          $"playing={transport.IsPlaying} " +
                          $"playingVideoId=\"{vm.PlayingVideoId}\" (stale) " +
                          $"status=\"{vm.StatusText}\"");

        ok &= Click(vm, entry, transport, "desync-click", probe: false);
        return ok;
    }

    /// <summary>One click through the view model, measured the same way.</summary>
    private static bool Click(
        YouTubeSearchViewModel vm,
        YtEntry entry,
        StreamPreviewTransport transport,
        string label,
        bool probe)
    {
        vm.PreviewCommand.Execute(entry);
        bool playing = WaitPlaying(transport, PlayTimeoutMs);
        if (!playing)
        {
            Say($"{label}: not playing \u2014 playing={transport.IsPlaying} " +
                              $"clock=\"{vm.PreviewClock}\" status=\"{vm.StatusText}\" " +
                              $"player=\"{transport.Status}\"");
            return false;
        }

        double first = transport.PositionSeconds;
        string audio = probe ? transport.SelfTestPlayer.AudioPathProbe() : "";
        Pump(SampleGapMs);
        double second = transport.PositionSeconds;
        double delta = second - first;

        Say($"{label}: t1={F(first)}s t2={F(second)}s delta={F(delta)}s " +
                          $"duration={F(transport.DurationSeconds)}s playing={transport.IsPlaying} " +
                          $"clock=\"{vm.PreviewClock}\" playingVideoId=\"{vm.PlayingVideoId}\" " +
                          $"status=\"{vm.StatusText}\"");
        if (probe) Say($"{label}: {audio}");
        return delta >= MinDelta;
    }

    private static bool WaitPlaying(StreamPreviewTransport transport, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (transport.IsPlaying) return true;
            Pump(100);
        }

        return transport.IsPlaying;
    }

    /// <summary>Real time with the dispatcher pumping: the player posts its state here.</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Flushed per line: a redirected stdout is block-buffered, and a
    /// hung run must still leave the measurements it got.</summary>
    private static void Say(string line)
    {
        Console.WriteLine(line);
        Console.Out.Flush();
    }

    private static void SayErr(string line)
    {
        Console.Error.WriteLine(line);
        Console.Error.Flush();
    }

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}" : "unparsable";

    private static string Tail(string text, int length) =>
        text.Length <= length ? text.Trim() : text[^length..].Trim();

    private static string VideoIdOf(string spec)
    {
        spec = spec.Trim();
        if (!spec.Contains('/') && !spec.Contains('=')) return spec;

        if (Uri.TryCreate(spec, UriKind.Absolute, out var uri))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals("v", StringComparison.OrdinalIgnoreCase))
                    return parts[1];
            }

            return uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : spec;
        }

        return spec;
    }
}

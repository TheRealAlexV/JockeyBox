using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JockeyBox.Dsp;
using JockeyBox.Tools;

namespace JockeyBox.Settings;

/// <summary>
/// How the lyrics panel follows playback: one line at a time (<see cref="Line"/>),
/// or word by word with the bouncing ball (<see cref="Word"/>).
/// Persisted as a number in settings.json.
/// </summary>
public enum LyricsFollowMode
{
    Line = 0,
    Word = 1,
}

/// <summary>
/// How the library browser groups tracks: by tag-derived artist/album
/// (<see cref="ArtistAlbum"/>), or by the folder layout on disk
/// (<see cref="Path"/>). Persisted as a number in settings.json.
/// </summary>
public enum LibraryLayout
{
    ArtistAlbum = 0,
    Path = 1,
}

/// <summary>
/// How the next queued track is brought in when the current one ends.
/// Persisted as a number in settings.json.
/// </summary>
public enum TransitionMode
{
    /// <summary>Hard cut at the end of the track.</summary>
    Off = 0,
    /// <summary>Automatic crossfade completing at the end of the track.</summary>
    AutoCrossfade = 1,
    /// <summary>Beat-matched, tempo-matched mix into the next track.</summary>
    AutoMix = 2,
}

/// <summary>
/// User settings, persisted as JSON in %APPDATA%\JockeyBox\settings.json.
/// The Spotify client secret is encrypted at rest with DPAPI (current user
/// scope) - it never touches disk in plaintext.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// The home this instance was read from (or created under). <see cref="Save()"/>
    /// goes back there, so a write that runs late — a debounce firing after
    /// teardown, a continuation after an <c>await</c> — cannot land in whichever
    /// profile <c>JOCKEYBOX_HOME</c> happens to name by then.
    /// </summary>
    private readonly string _home = AppPaths.Home;
    // Library: one tree of music files. The karaoke version of a track sits
    // beside it as "Song.karaoke.ext" (Library.KaraokeNaming); KaraokeDirectory
    // is only the legacy split root and is normalised to the music root when it
    // is unset or points at the same folder.
    public string MusicDirectory { get; set; } = "";
    public string KaraokeDirectory { get; set; } = "";

    /// <summary>Legacy (pre-1.1) single root that contained \Original + \Karaoke.</summary>
    public string RootDirectory { get; set; } = "";

    /// <summary>
    /// Browser tree layout. Missing or undefined values (older settings files,
    /// hand edits) fall back to <see cref="LibraryLayout.ArtistAlbum"/> in
    /// <see cref="Load"/>.
    /// </summary>
    public LibraryLayout LibraryLayout { get; set; } = LibraryLayout.ArtistAlbum;

    // Per-deck audio routing / sync. A deck's output device is a route field -
    // MainOutputDeviceId for deck A, KaraokeAidOutputDeviceId for deck B - not a
    // per-deck setting, so the deck combos and Settings cannot disagree.
    public double DeckADelayMs { get; set; }
    public double DeckBDelayMs { get; set; }

    /// <summary>
    /// Per-deck output delay in ms (0…2000): how far this deck's audio lags what
    /// the deck produces. Distinct from <see cref="DeckADelayMs"/> /
    /// <see cref="DeckBDelayMs"/>, which trim the karaoke stem inside the deck.
    /// </summary>
    public double DeckAOutputDelayMs { get; set; }
    public double DeckBOutputDelayMs { get; set; }
    public float DeckAVolume { get; set; } = 1.0f;
    public float DeckBVolume { get; set; } = 1.0f;

    /// <summary>Per-device delay offsets (device ID → ms delta from master clock).</summary>
    public Dictionary<string, double> DeviceDelays { get; set; } = new();

    /// <summary>
    /// Per-device output delay in ms, keyed by endpoint id (0…2000). Applied to the
    /// sum of every bus routed to that device, after the buses and before its ring:
    /// delay the wired path until the Bluetooth path catches up. Distinct from
    /// <see cref="DeviceDelays"/>, which is the deck card's per-device sync trim -
    /// that one shifts what the deck plays, this one shifts what the device emits.
    /// </summary>
    public Dictionary<string, double> DeviceOutputDelays { get; set; } = new();

    // ------------------------------------------------------------------
    // 2.0 audio routing.
    //
    // One program bus carries the whole mix (decks + soundboard) to a single
    // output endpoint - that is the device OBS records. The two optional
    // outputs and the per-deck sends are extra tee'd endpoints; every one of
    // them is off when its id is null/empty ("None" in the UI).
    //
    // The 1.x per-deck send device fields (DeckADeviceId/DeckBDeviceId) are gone
    // as of schema 5: the deck selectors now write the route fields below, so a
    // deck choice and a Settings choice are one value. Schema 5 drops the old
    // keys and never promotes them - on some profiles they disagree with where
    // the deck actually plays.
    // ------------------------------------------------------------------

    /// <summary>Bumped when a migration in <see cref="MigrateAudioRouting"/> is needed.</summary>
    public int SettingsSchemaVersion { get; set; } = 5;

    /// <summary>
    /// True once the settings dialog has stored a routing choice (including
    /// "None"). Until then an all-empty profile is treated as "not configured
    /// yet" and the program bus is routed to the system default device, so an
    /// upgrade never boots into silence. See <c>DualDeckTransport.BuildRoute</c>.
    /// </summary>
    public bool AudioRoutingConfigured { get; set; }

    /// <summary>
    /// Deck A Output's device: deck A only (no microphones, no deck B); null = None.
    /// The JSON key keeps its 2.1 name so an upgraded install does not lose its
    /// routing; in the UI and the engine this is "Deck A Output".
    /// </summary>
    public string? MainOutputDeviceId { get; set; }

    /// <summary>Legacy 2.0 name for <see cref="MainOutputDeviceId"/>; migrated on load.</summary>
    public string? ProgramOutputDeviceId { get; set; }

    /// <summary>
    /// Deck B Output's device: deck B only; null = None. Keeps its 2.1 JSON key
    /// (<c>KaraokeAidOutputDeviceId</c>) for the same migration reason as Deck A:
    /// this bus used to be the karaoke cue and is now deck B's real output.
    /// </summary>
    public string? KaraokeAidOutputDeviceId { get; set; }

    /// <summary>Recording Device output: every unmuted microphone; null = None.</summary>
    public string? RecordingOutputDeviceId { get; set; }

    /// <summary>Soundboard output; null = None (falls back to Deck A if SoundboardFollowsMain).</summary>
    public string? SoundboardOutputDeviceId { get; set; }

    /// <summary>When the soundboard has no device of its own, play it on Deck A.</summary>
    public bool SoundboardFollowsMain { get; set; } = true;

    /// <summary>
    /// MON: when on, the Monitor bus carries deck A × <see cref="MonitorAGain"/>
    /// plus deck B × <see cref="MonitorBGain"/> as well as the monitored
    /// microphones. Off by default: an install with no cue headphones must not
    /// send deck audio to the monitor endpoint.
    /// </summary>
    public bool MonitorEnabled { get; set; }

    /// <summary>Deck A's send into the Monitor bus (0..1.2).</summary>
    public float MonitorAGain { get; set; } = 0.7f;

    /// <summary>Deck B's send into the Monitor bus (0..1.2).</summary>
    public float MonitorBGain { get; set; } = 0.7f;

    /// <summary>
    /// Recording devices ("microphones"), each independently enabled with its own
    /// app-domain volume, mute and monitor. Replaces the single <see cref="MicDeviceId"/>.
    /// </summary>
    public List<MicDeviceSettings> MicDevices { get; set; } = new();

    /// <summary>
    /// Which of <see cref="MicDevices"/> the main-window mic strip edits. Null =
    /// the first enabled device. A saved id that is no longer present is kept:
    /// the strip stays bound to it and reports the device as unavailable.
    /// </summary>
    public string? SelectedMicDeviceId { get; set; }
    /// <summary>Optional monitor copy, taken before the global delay; null = None.</summary>
    public string? MonitorOutputDeviceId { get; set; }

    /// <summary>
    /// Per-deck send to its own endpoint (deck A). On by default: the button
    /// switches a deck's cue <i>off</i>, and an install that has never written
    /// the flag keeps cueing both decks.
    /// </summary>
    public bool DeckACueEnabled { get; set; } = true;
    /// <summary>Per-deck send to its own endpoint (deck B). See <see cref="DeckACueEnabled"/>.</summary>
    public bool DeckBCueEnabled { get; set; } = true;

    /// <summary>Master fader for every output; 0..1.2.</summary>
    public float MasterVolume { get; set; } = 1.0f;

    /// <summary>
    /// Per-channel mixer chains in the deck render path. Off means the engine skips
    /// the chains entirely, so the output is bit-identical to a build without them.
    /// <para>On by default, which is safe: a device with no stored strip runs a
    /// neutral strip, a neutral strip is byte-transparent (proven by test), and no
    /// limiter is constructed unless one is enabled. So on-by-default adds exactly
    /// zero latency and cannot change the sound until a parameter is moved.</para>
    /// </summary>
    public bool MixerEnabled { get; set; } = true;

    // MixerEnabled is additive on schema 5: an existing settings.json that already
    // holds "MixerEnabled": false keeps that value (the deploy step sets the live
    // file explicitly), and a file without the key gets the default above. No
    // migration, so do not bump SettingsSchemaVersion for it.

    // ------------------------------------------------------------------
    // Virtual ASIO driver feed (2.3).
    //
    // The native driver reads Local\JockeyBox.Bus.<name> and serves it as ASIO
    // inputs. These flags choose which bus publishes into that ring. Additive on
    // schema 5, same precedent as MixerEnabled: a settings.json without the keys
    // deserialises to false, so no install starts writing the ring by accident.
    // A bus can be both a physical output and the ring; the flag never replaces
    // a routing choice.
    // ------------------------------------------------------------------

    /// <summary>Deck A's bus also publishes to the virtual ASIO ring.</summary>
    public bool VirtualAsioDeckA { get; set; }

    /// <summary>Deck B's bus also publishes to the virtual ASIO ring.</summary>
    public bool VirtualAsioDeckB { get; set; }

    /// <summary>The recording-device bus also publishes to the virtual ASIO ring.</summary>
    public bool VirtualAsioRecording { get; set; }

    /// <summary>The soundboard bus also publishes to the virtual ASIO ring.</summary>
    public bool VirtualAsioSoundboard { get; set; }

    /// <summary>The monitor (operator cue) bus also publishes to the virtual ASIO ring.</summary>
    public bool VirtualAsioMonitor { get; set; }

    /// <summary>
    /// Section name of the ring, without the <c>Local\JockeyBox.Bus.</c> prefix. Must
    /// match the driver's <c>JOCKEYBOX_ASIO_BUS</c>, whose default is "main".
    /// </summary>
    public string VirtualAsioBusName { get; set; } = "main";

    // One ring, so one bus publishes: with several flags set the engine uses the
    // first in routing order and names the ignored buses in the log.

    // ------------------------------------------------------------------
    // Talk-over microphone (2.0).
    //
    // The voice is mixed into the program bus inside the engine, so it reaches
    // OBS on the same stream as the music and with the same OutputDelayMs. The
    // level here is an app-domain gain: the Windows recording level of the
    // device is never modified.
    //
    // MicDeviceId is a *capture* endpoint id; null/empty = None. A stored id
    // whose device is unplugged stays stored and resolves to "no microphone"
    // (silence), it is never silently re-routed to another mic.
    // ------------------------------------------------------------------

    /// <summary>Capture endpoint for the talk-over voice; null = None.</summary>
    public string? MicDeviceId { get; set; }

    /// <summary>App-domain microphone gain, 0..2 (0..200%), default 1.0.</summary>
    public float MicLevel { get; set; } = 1.0f;

    /// <summary>
    /// Also send the voice to the monitor output (pre-delay cue). Off by default:
    /// monitoring a microphone through an open speaker feeds back.
    /// </summary>
    public bool MicMonitorEnabled { get; set; }

    /// <summary>
    /// Whether the microphone choice was made explicitly (settings dialog) or
    /// adopted once from the system default. Only the one-time adoption looks at
    /// the default: after that a null <see cref="MicDeviceId"/> means None, and a
    /// saved id is never swapped for a different device.
    /// </summary>
    public bool MicAutoSelected { get; set; }

    /// <summary>
    /// Global delay applied to the program and second outputs, in milliseconds.
    /// Compensates the A/V offset against OBS video capture. The monitor output
    /// is deliberately not delayed, and the UI clock reports the delayed time.
    /// </summary>
    public int OutputDelayMs { get; set; }

    // ---- three-band mix EQ -------------------------------------------------
    // Low shelf 70 Hz, mid bell 1 kHz (Q 0.6), high shelf 13 kHz, each -50…+50 dB
    // with 0 dB (the centre detent, a true bypass) as the default. Symmetric: the
    // operator asked for the same cut and boost either way.
    //
    // Clamped on load in PostProcess, never clamped on write, so a settings file
    // that was hand-edited out of range cannot leave the engine with a gain the
    // UI cannot display, and an existing unknown key is never dropped.

    /// <summary>Low-shelf gain, dB (-50…+50, 0 = bypass).</summary>
    public double EqLowDb { get; set; }

    /// <summary>Mid-bell gain, dB (-50…+50, 0 = bypass).</summary>
    public double EqMidDb { get; set; }

    /// <summary>High-shelf gain, dB (-50…+50, 0 = bypass).</summary>
    public double EqHighDb { get; set; }

    /// <summary>How a queued track is brought in when the current one ends.</summary>
    public TransitionMode TransitionMode { get; set; } = TransitionMode.AutoCrossfade;

    /// <summary>Requested crossfade length, seconds (the tempo-mismatch cap still applies).</summary>
    public double TransitionFadeSeconds { get; set; } = 2.0;

    /// <summary>Maximum tempo change auto-mix will apply to match two tracks, in percent.</summary>
    public double TempoRangePercent { get; set; } = 8.0;

    /// <summary>Per-deck tempo slider range, in percent (centre = 0).</summary>
    public double ManualTempoRangePercent { get; set; } = 16.0;

    /// <summary>Global gain for the soundboard bus; 0..1.2.</summary>
    public float SoundboardVolume { get; set; } = 1.0f;

    /// <summary>Lower the music bed while a soundboard sound plays.</summary>
    public bool SoundboardDuckDecks { get; set; }

    /// <summary>Loopback port for the Stream Deck / automation API; 0 = disabled.</summary>
    public int ApiPort { get; set; } = 28100;

    /// <summary>Bearer token the localhost API requires (generated on first save).</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>
    /// Cue recall chords, keyed "cue.a.1".."cue.a.8"/"cue.a.q" (deck B likewise).
    /// A missing key falls back to <c>CueHotkeys.DefaultChord</c>; an empty value
    /// leaves that cue deliberately unbound.
    /// </summary>
    public Dictionary<string, string> CueHotkeys { get; set; } = new();

    // Vocal removal engine (UVR models via the audio-separator CLI)
    /// <summary>Optional explicit python.exe used to build the engine venv; empty = auto-detect.</summary>
    public string PythonPath { get; set; } = "";
    /// <summary>Install the CUDA (NVIDIA GPU) build of the engine - much faster, ~3 GB download.</summary>
    public bool SeparationUseGpu { get; set; }

    // Lyrics
    public string LyricsApiBaseUrl { get; set; } = "";

    // Spotify OAuth PKCE (replaces client ID/secret)
    /// <summary>OAuth access token (short-lived, auto-refreshed).</summary>
    [JsonIgnore]
    public string SpotifyAccessToken { get; set; } = "";
    public string SpotifyAccessTokenProtected { get; set; } = "";

    /// <summary>Refresh token (long-lived, used to get new access tokens).</summary>
    public string SpotifyRefreshToken { get; set; } = "";

    /// <summary>Absolute UTC time when the current access token expires.</summary>
    public DateTime SpotifyTokenExpiry { get; set; } = DateTime.MinValue;

    // Legacy client credentials (kept for backward compat)
    public string SpotifyClientId { get; set; } = "";

    /// <summary>Runtime plaintext secret; only the DPAPI-protected form is serialized.</summary>
    [JsonIgnore]
    public string SpotifyClientSecret { get; set; } = "";

    /// <summary>DPAPI-encrypted (CurrentUser) client secret, base64.</summary>
    public string SpotifyClientSecretProtected { get; set; } = "";

    /// <summary>Migration shim: reads the plaintext field from pre-DPAPI settings files.</summary>
    [JsonPropertyName("SpotifyClientSecret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPlaintextSecret { get; set; }

    /// <summary>
    /// Global visual offset applied to synced lyrics, in milliseconds.
    /// <para>Semantic contract: <b>positive = lyrics appear LATER</b> (delayed).</para>
    /// </summary>
    /// <remarks>
    /// The lyric highlighter compares a line's timestamp against the playback
    /// position, so the offset has to be applied negated:
    /// <c>lyric.Time &lt;= position + EffectiveOffset</c>. Adding this property
    /// directly to the position inverts the contract (positive would make lyrics
    /// early), which is what the pre-1.2 highlighter did. Do not change this
    /// property's sign; add <see cref="EffectiveOffset"/> instead.
    /// </remarks>
    public double LyricsOffsetMs { get; set; }

    /// <summary>
    /// <see cref="LyricsOffsetMs"/> already negated for the "add to playback
    /// position" idiom: <c>position + EffectiveOffset</c>. Positive setting
    /// (later lyrics) produces a negative <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan EffectiveOffset => TimeSpan.FromMilliseconds(-LyricsOffsetMs);

    /// <summary>
    /// Upper bound for lyric offsets (global and per track), in milliseconds.
    /// </summary>
    public const double MaxLyricsOffsetMs = 10_000;

    /// <summary>
    /// Per-track lyric offsets in milliseconds, keyed by
    /// <c>TrackEntry.RelativeKey</c>. Positive = lyrics later, same contract as
    /// <see cref="LyricsOffsetMs"/>. A missing key falls back to the global value.
    /// </summary>
    public Dictionary<string, double> LyricsOffsetByTrack { get; set; } = new();

    /// <summary>
    /// Per-track preferred lyrics provider id, keyed by
    /// <c>TrackEntry.RelativeKey</c> (values: "lrc", "spotify-color",
    /// "spotify-api", "lrclib"). Missing key = use the default chain.
    /// </summary>
    public Dictionary<string, string> LyricsSourceByTrack { get; set; } = new();

    /// <summary>
    /// Line or word-by-word lyric highlight. Global (not per track); an
    /// out-of-range value read from disk is normalised to <see cref="Line"/> by
    /// <see cref="Load"/>.
    /// </summary>
    public LyricsFollowMode LyricsFollowMode { get; set; } = LyricsFollowMode.Line;

    /// <summary>
    /// Offset for a track in milliseconds; unknown/empty key falls back to the
    /// global <see cref="LyricsOffsetMs"/>. Positive = lyrics later.
    /// </summary>
    public double GetLyricsOffset(string? trackKey)
    {
        if (!string.IsNullOrEmpty(trackKey)
            && LyricsOffsetByTrack != null
            && LyricsOffsetByTrack.TryGetValue(trackKey, out var ms))
            return ms;

        return LyricsOffsetMs;
    }

    /// <summary>
    /// Per-track offset negated for <c>position +</c> (see <see cref="EffectiveOffset"/>).
    /// </summary>
    public TimeSpan GetEffectiveOffset(string? trackKey) =>
        TimeSpan.FromMilliseconds(-GetLyricsOffset(trackKey));

    /// <summary>
    /// Store a per-track offset (clamped to +/-<see cref="MaxLyricsOffsetMs"/>).
    /// An empty key writes the global value instead.
    /// </summary>
    public void SetLyricsOffset(string? trackKey, double ms)
    {
        ms = Math.Clamp(ms, -MaxLyricsOffsetMs, MaxLyricsOffsetMs);
        if (string.IsNullOrEmpty(trackKey))
        {
            LyricsOffsetMs = ms;
            return;
        }

        (LyricsOffsetByTrack ??= new())[trackKey] = ms;
    }

    /// <summary>
    /// Keep the pop-out lyrics window above other windows. Off by default: an
    /// always-on-top surface that the operator did not ask for is a nuisance.
    /// </summary>
    public bool LyricsWindowTopmost { get; set; }

    // ---- pop-out lyrics window geometry ----------------------------------
    // Null/NaN means "never placed": the window falls back to CenterOwner at its
    // design size instead of restoring a stale position, e.g. on a monitor that
    // is no longer attached.

    /// <summary>Saved window left edge in screen pixels, or null.</summary>
    public double? LyricsWindowLeft { get; set; }

    /// <summary>Saved window top edge in screen pixels, or null.</summary>
    public double? LyricsWindowTop { get; set; }

    /// <summary>Saved window width in pixels, or null.</summary>
    public double? LyricsWindowWidth { get; set; }

    /// <summary>Saved window height in pixels, or null.</summary>
    public double? LyricsWindowHeight { get; set; }

    /// <summary>Preferred provider id for a track, or null when none is stored.</summary>
    public string? GetLyricsSource(string? trackKey)
    {
        if (string.IsNullOrEmpty(trackKey) || LyricsSourceByTrack == null) return null;
        return LyricsSourceByTrack.TryGetValue(trackKey, out var id) && !string.IsNullOrWhiteSpace(id)
            ? id
            : null;
    }

    /// <summary>Remember a provider id for a track; null/blank clears the entry.</summary>
    public void SetLyricsSource(string? trackKey, string? sourceId)
    {
        if (string.IsNullOrEmpty(trackKey)) return;

        LyricsSourceByTrack ??= new();
        if (string.IsNullOrWhiteSpace(sourceId)) LyricsSourceByTrack.Remove(trackKey);
        else LyricsSourceByTrack[trackKey] = sourceId.Trim();
    }

    /// <summary>
    /// Move per-track lyrics settings from one library key to another after a
    /// rename/move. The new key wins when both exist (the moved file is the one
    /// the user is looking at). Returns how many entries moved; nothing is saved
    /// here — the caller owns persistence, like the rest of the media path
    /// repointing.
    /// </summary>
    public int RepointLyricsKey(string? oldKey, string? newKey)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)) return 0;

        var from = oldKey.Trim();
        var to = newKey.Trim();
        if (string.Equals(from, to, StringComparison.Ordinal)) return 0;

        var moved = 0;
        moved += RepointValue(LyricsOffsetByTrack, from, to);
        moved += RepointValue(LyricsSourceByTrack, from, to);
        return moved;
    }

    private static int RepointValue<T>(Dictionary<string, T>? map, string from, string to)
    {
        if (map == null || map.Count == 0) return 0;

        var keys = map.Keys
            .Where(key => string.Equals(key, from, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (keys.Count == 0) return 0;

        foreach (var key in keys)
        {
            var value = map[key];
            map.Remove(key);
            map[to] = value;
        }

        return keys.Count;
    }

    // ---- source downloads (Spotify / YouTube) ------------------------------
    //
    // The app fetches its own yt-dlp/ffmpeg into %APPDATA%\JockeyBox\tools on
    // first use; an explicit path here wins over the bundled copy, which wins
    // over a tool found on PATH.

    /// <summary>Explicit yt-dlp.exe; empty = bundled tools dir, then PATH.</summary>
    public string YtDlpPath { get; set; } = "";
    /// <summary>Explicit ffmpeg.exe; empty = bundled tools dir, then PATH.</summary>
    public string FfmpegPath { get; set; } = "";
    public string YtDlpVersion { get; set; } = "";
    public string FfmpegVersion { get; set; } = "";

    /// <summary>artist-album | artist | playlist</summary>
    public string DownloadLayoutPreset { get; set; } = "artist-album";
    /// <summary>Optional custom template; empty = use the preset.</summary>
    public string DownloadLayoutTemplate { get; set; } = "";
    public int DownloadBitrateKbps { get; set; } = 320;
    public int DownloadConcurrency { get; set; } = 2;
    public bool DownloadSkipExisting { get; set; } = true;
    public bool DownloadAutoKaraoke { get; set; }
    /// <summary>chrome | edge | firefox | "" (none) - passed to yt-dlp --cookies-from-browser.</summary>
    public string DownloadCookiesBrowser { get; set; } = "";
    /// <summary>LRU cap for streamed source audio.</summary>
    public int StreamCacheLimitMb { get; set; } = 2048;

    /// <summary>Space separated scopes Spotify actually granted on the last login.</summary>
    public string SpotifyGrantedScopes { get; set; } = "";
    public string SpotifyClientIdOverride { get; set; } = "";
    /// <summary>Loopback port for the OAuth callback; must match a Redirect URI on that app.</summary>
    public int SpotifyRedirectPort { get; set; } = 8888;
    /// <summary>0 = automatic playlist sync disabled.</summary>
    public int YouTubeAutoSyncMinutes { get; set; }
    public int YouTubeSearchResults { get; set; } = 25;

    // ---------------------------------------------------------------

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JockeyBox.v1");
    /// <summary>Entropy used by pre-rename builds; read-only migration path.</summary>
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("SplitPlayerK.v1");

    /// <summary>The document this process reads and writes, under <see cref="AppPaths.Home"/>.</summary>
    public static string SettingsPath => Path.Combine(AppPaths.Home, "settings.json");

    public static AppSettings Load() => TryLoad() ?? PostProcess(new AppSettings());

    /// <summary>
    /// Like <see cref="Load"/>, but returns <c>null</c> instead of defaults when the
    /// file exists and cannot be read. A merge must be able to tell "the file says
    /// nothing" from "I could not read the file": the first is a value, the second
    /// must never overwrite live settings with defaults.
    /// </summary>
    public static AppSettings? TryLoad()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { return null; }

        return PostProcess(settings);
    }

    /// <summary>
    /// Secret unprotection, defensive normalization and the one-time schema
    /// migrations. Every load runs it; it only ever fills in or upgrades values,
    /// so it cannot drop a setting the caller is about to write.
    /// </summary>
    private static AppSettings PostProcess(AppSettings settings)
    {
        // Prefer the protected secret; fall back to (and migrate away from)
        // a legacy plaintext value.
        if (!string.IsNullOrEmpty(settings.SpotifyClientSecretProtected))
            settings.SpotifyClientSecret = Unprotect(settings.SpotifyClientSecretProtected);
        else if (!string.IsNullOrEmpty(settings.LegacyPlaintextSecret))
        {
            settings.SpotifyClientSecret = settings.LegacyPlaintextSecret;
            settings.Save(); // immediately rewrite with the secret encrypted
        }
        settings.LegacyPlaintextSecret = null;

        // Older/hand-edited files may omit these or contain an explicit null.
        settings.LyricsOffsetByTrack ??= new();
        settings.LyricsSourceByTrack ??= new();

        // Cue chords seed once, so the operator has something to edit in the
        // settings file; an emptied map stays empty (every cue unbound on purpose).
        settings.CueHotkeys ??= new();
        if (settings.CueHotkeys.Count == 0)
            settings.CueHotkeys = JockeyBox.Playback.CueHotkeys.Defaults();

        // Unknown/removed follow modes (e.g. from a newer build or a hand-edited
        // file) must not leave an undefined enum value behind.
        if (!Enum.IsDefined(settings.LyricsFollowMode))
            settings.LyricsFollowMode = LyricsFollowMode.Line;

        // Same for a layout name this build no longer knows.
        if (!Enum.IsDefined(settings.LibraryLayout))
            settings.LibraryLayout = LibraryLayout.ArtistAlbum;

        // A hand-edited or newer-build mic level must not leave the engine with
        // a boost it cannot represent (the engine clamps too, this keeps the
        // persisted value honest).
        settings.MicLevel = Math.Clamp(settings.MicLevel, 0f, 2f);

        // The mix EQ runs -50…+50 dB and 0 dB is a true bypass. A hand-edited or
        // newer-build value outside that window is clamped here so the engine never
        // receives a gain the UI cannot represent - without dropping the key.
        settings.EqLowDb = ClampEqDb(settings.EqLowDb);
        settings.EqMidDb = ClampEqDb(settings.EqMidDb);
        settings.EqHighDb = ClampEqDb(settings.EqHighDb);

        // A per-deck output delay outside 0…2000 ms is not something the engine can
        // represent (its delay line is 2 s), so keep the persisted value honest the
        // same way the EQ does - without dropping the key.
        settings.DeckAOutputDelayMs = ClampDeckOutputDelayMs(settings.DeckAOutputDelayMs);
        settings.DeckBOutputDelayMs = ClampDeckOutputDelayMs(settings.DeckBOutputDelayMs);
        settings.DeviceOutputDelays = ClampDeviceOutputDelays(settings.DeviceOutputDelays);
        if (string.IsNullOrWhiteSpace(settings.MicDeviceId)) settings.MicDeviceId = null;

        // Unprotect OAuth access token
        if (!string.IsNullOrEmpty(settings.SpotifyAccessTokenProtected))
            settings.SpotifyAccessToken = Unprotect(settings.SpotifyAccessTokenProtected);

        settings.MigrateLegacyRoot();
        settings.MigrateAudioRouting();
        settings.NormalizeKaraokeRoot();
        return settings;
    }

    /// <summary>
    /// A detached copy for change detection: property values are copied, so the
    /// <c>[JsonIgnore]</c> runtime fields (plaintext OAuth token and secret) come
    /// along and a later save cannot blank the protected copies.
    ///
    /// <para>Collections are copied by reference on purpose: the settings window
    /// replaces <see cref="MicDevices"/> with a fresh list rather than mutating
    /// this one, so the snapshot keeps seeing the rows the dialog was built from.</para>
    /// </summary>
    public AppSettings Clone()
    {
        var copy = new AppSettings();
        copy.CopyFrom(this);

        // The per-device maps are value maps, not view collections: the Settings
        // dialog edits entries in place. A shallow clone would share them with the
        // snapshot, so the three-way merge would see an edit as "unchanged" and
        // keep the disk copy - i.e. silently drop the operator's new delay.
        copy.DeviceDelays = new Dictionary<string, double>(
            DeviceDelays ?? new(), StringComparer.OrdinalIgnoreCase);
        copy.DeviceOutputDelays = new Dictionary<string, double>(
            DeviceOutputDelays ?? new(), StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>Overwrite every writable property from <paramref name="other"/>, keeping this instance's identity.</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite)
                property.SetValue(this, property.GetValue(other));
        }
    }

    /// <summary>
    /// <summary>
    /// Carries an older settings file up to the current schema. Every step only
    /// fills in what the file could not have known; none of them may change a
    /// route field's value, because the engine is already playing that value.
    /// </summary>
    private void MigrateAudioRouting()
    {
        if (string.IsNullOrEmpty(ApiToken)) ApiToken = NewApiToken();

        if (SettingsSchemaVersion >= 5) return;

        if (SettingsSchemaVersion < 4)
        {
            // 2.0 had a single "program" bus and one mic. 2.1 splits the buses and
            // turns the microphone into a list, so carry the old choices across:
            // program becomes Main, the saved mic becomes the first mixer entry
            // (monitor off - monitoring a mic through speakers feeds back).
            //
            // The pre-2.0 deck send ids (DeckADeviceId/DeckBDeviceId) are not a
            // route and are never promoted: on a real profile they can disagree
            // with where the deck actually plays, and promoting them would move
            // that deck to the wrong device. See the schema 4 -> 5 note below.
            if (string.IsNullOrEmpty(MainOutputDeviceId) && !string.IsNullOrEmpty(ProgramOutputDeviceId))
                MainOutputDeviceId = ProgramOutputDeviceId;
            ProgramOutputDeviceId = null;

            if (!string.IsNullOrEmpty(MicDeviceId))
            {
                if (!MicDevices.Any(m => string.Equals(m.DeviceId, MicDeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    MicDevices.Add(new MicDeviceSettings
                    {
                        DeviceId = MicDeviceId!,
                        Volume = Math.Clamp(MicLevel, 0f, 2f),
                        Muted = false,
                        Monitor = MicMonitorEnabled,
                    });
                }
                MicDeviceId = null;
            }

            MicDevices.RemoveAll(m => string.IsNullOrWhiteSpace(m.DeviceId));
            foreach (var mic in MicDevices)
            {
                mic.DeviceId = mic.DeviceId.Trim();
                mic.Volume = Math.Clamp(mic.Volume, 0f, 2f);
            }

            // The old per-deck sends are gone: Karaoke Aid replaces them.
            DeckACueEnabled = false;
            DeckBCueEnabled = false;
        }

        // Schema 4 -> 5: the per-deck send ids no longer exist as settings, so the
        // save below rewrites the file without them. They are deliberately not
        // promoted onto Main/Karaoke Aid: on a real profile they disagree with
        // where the deck actually plays, so a promotion is the one migration that
        // could move a working deck to the wrong output.

        SettingsSchemaVersion = 5;
        Save();
    }

    /// <summary>128-bit hex token for the localhost API.</summary>
    private static string NewApiToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Read a blank karaoke root as "the same folder as the music", and a
    /// differently spelled path that points at the music root as that root's
    /// form. The merged library then has one root everywhere - the scan skips the
    /// second pass, and callers that still ask for <see cref="KaraokeDirectory"/>
    /// (the settings dialog, the separation gate, legacy path mapping) get the
    /// folder that actually holds the files.
    ///
    /// A genuinely different folder is kept: an app updated before the migration
    /// runs must still see the old split layout.
    /// </summary>
    /// <summary>
    /// Clamp one EQ band gain to the range the hardware convention uses. Also maps
    /// NaN/±∞ to the centre detent, so a corrupt file cannot poison the filters.
    /// </summary>
    private static double ClampEqDb(double db)
        => double.IsNaN(db) ? 0.0 : Math.Clamp(db, -50.0, 50.0);

    /// <summary>
    /// Clamp one deck's output delay to the range the engine's delay line can hold.
    /// NaN/±∞ map to "no delay", so a corrupt file cannot poison the buffer.
    /// </summary>
    private static double ClampDeckOutputDelayMs(double ms)
        => double.IsFinite(ms) ? Math.Clamp(ms, 0, JockeyBox.Audio.DeckChannel.MaxOutputDelayMs) : 0.0;

    /// <summary>
    /// Keep the persisted per-device delays inside the delay line's own window and
    /// drop keys no device could use. A null dictionary (hand edit, older shape)
    /// becomes empty rather than crashing the first lookup.
    /// </summary>
    private static Dictionary<string, double> ClampDeviceOutputDelays(Dictionary<string, double>? delays)
    {
        var clean = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (delays == null) return clean;

        foreach (var (id, ms) in delays)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            clean[id] = double.IsFinite(ms)
                ? Math.Clamp(ms, 0, JockeyBox.Audio.Graph.DelayBuffer.MaxDelayMs)
                : 0.0;
        }
        return clean;
    }

    public void NormalizeKaraokeRoot()
    {
        if (string.IsNullOrWhiteSpace(MusicDirectory)) return;

        if (string.IsNullOrWhiteSpace(KaraokeDirectory))
        {
            KaraokeDirectory = MusicDirectory;
            return;
        }

        if (SameDirectory(KaraokeDirectory, MusicDirectory)) KaraokeDirectory = MusicDirectory;
    }

    /// <summary>True when two folder settings name the same place (tolerating "\" vs "/" and a trailing separator).</summary>
    private static bool SameDirectory(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Pre-1.1 versions stored one RootDirectory containing \Original and
    /// \Karaoke; map those subfolders onto the new two-folder model once.
    /// </summary>
    private void MigrateLegacyRoot()
    {
        if (!string.IsNullOrWhiteSpace(MusicDirectory) || string.IsNullOrWhiteSpace(RootDirectory))
            return;

        var music = Path.Combine(RootDirectory, "Original");
        var karaoke = Path.Combine(RootDirectory, "Karaoke");
        if (Directory.Exists(music)) MusicDirectory = music;
        if (Directory.Exists(karaoke)) KaraokeDirectory = karaoke;
        if (MusicDirectory.Length > 0)
        {
            RootDirectory = "";
            Save();
        }
    }

    public void Save() => Save(_home);

    /// <summary>
    /// Write to <paramref name="home"/> instead of whatever <c>JOCKEYBOX_HOME</c>
    /// holds right now. A debounced save captures its home when it is scheduled
    /// and passes it here, so a fire that lands after teardown cannot write this
    /// object over the profile someone else has since pointed the process at.
    /// </summary>
    public void Save(string home)
    {
        string path = Path.Combine(home, "settings.json");
        try
        {
            SpotifyClientSecretProtected = Protect(SpotifyClientSecret);
            SpotifyAccessTokenProtected = Protect(SpotifyAccessToken);
            LegacyPlaintextSecret = null; // never write plaintext back out
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // Best-effort by design, but not silent: a save that did not land is
            // why a setting can read back as its old value with no other clue.
            Log.Error($"settings: could not write {path}", ex);
        }
    }

    private static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch { return ""; }
    }

    private static string Unprotect(string protectedBase64)
    {
        try
        {
            var raw = Convert.FromBase64String(protectedBase64);
            try
            {
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                // Written by a pre-rename build with different entropy - retry once.
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(raw, LegacyEntropy, DataProtectionScope.CurrentUser));
            }
        }
        catch { return ""; } // wrong user/machine or corrupt - treat as unset
    }

/// <summary>
/// One recording device in the per-device mixer: <see cref="Volume"/> is the
/// app-domain gain (the Windows level is never touched), <see cref="Muted"/>
/// silences the device on every bus except Karaoke Aid/Monitoring when
/// <see cref="Monitor"/> is on, and <see cref="Monitor"/> sends it to the
/// monitoring output only. Monitor is off by default.
/// </summary>
public sealed class MicDeviceSettings
{
    public string DeviceId { get; set; } = "";
    public float Volume { get; set; } = 1.0f;
    public bool Muted { get; set; }
    public bool Monitor { get; set; }

    // Additive on the schema, the same precedent as MixerEnabled: a settings.json that
    // already exists has no "Channel" and deserialises to -1, meaning the whole device.
    // -1 keeps every saved device on the strip it has today.
    public int Channel { get; set; } = -1;

    // How Channel is read: one source channel duplicated to both sides (the default), or
    // Channel and Channel + 1 as a stereo pair. Additive on the schema too - a file with no
    // "Mode" deserialises to Mono, so every saved device keeps the strip it has today.
    public AsioChannelMode Mode { get; set; } = AsioChannelMode.Mono;
}
}

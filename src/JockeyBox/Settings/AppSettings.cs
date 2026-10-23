using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
/// User settings, persisted as JSON in %APPDATA%\JockeyBox\settings.json.
/// The Spotify client secret is encrypted at rest with DPAPI (current user
/// scope) - it never touches disk in plaintext.
/// </summary>
public class AppSettings
{
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

    // Per-deck audio routing / sync
    public string? DeckADeviceId { get; set; }
    public string? DeckBDeviceId { get; set; }
    public double DeckADelayMs { get; set; }
    public double DeckBDelayMs { get; set; }
    public float DeckAVolume { get; set; } = 1.0f;
    public float DeckBVolume { get; set; } = 1.0f;

    /// <summary>Per-device delay offsets (device ID → ms delta from master clock).</summary>
    public Dictionary<string, double> DeviceDelays { get; set; } = new();

    // Vocal removal engine (UVR models via the audio-separator CLI)
    /// <summary>Optional explicit python.exe used to build the engine venv; empty = auto-detect.</summary>
    public string PythonPath { get; set; } = "";
    /// <summary>UVR model filename passed to audio-separator (auto-downloaded on first use).</summary>
    public string SeparationModel { get; set; } = "UVR-MDX-NET-Inst_HQ_3.onnx";
    /// <summary>Install the CUDA (NVIDIA GPU) build of the engine - much faster, ~3 GB download.</summary>
    public bool SeparationUseGpu { get; set; }
    /// <summary>Output format for generated karaoke files (FLAC = lossless, no ffmpeg needed).</summary>
    public string SeparationOutputFormat { get; set; } = "FLAC";

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

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JockeyBox", "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings = new();
        try
        {
            if (File.Exists(SettingsPath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { /* corrupt settings - use defaults */ }

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

        // Unknown/removed follow modes (e.g. from a newer build or a hand-edited
        // file) must not leave an undefined enum value behind.
        if (!Enum.IsDefined(settings.LyricsFollowMode))
            settings.LyricsFollowMode = LyricsFollowMode.Line;

        // Same for a layout name this build no longer knows.
        if (!Enum.IsDefined(settings.LibraryLayout))
            settings.LibraryLayout = LibraryLayout.ArtistAlbum;

        // Unprotect OAuth access token
        if (!string.IsNullOrEmpty(settings.SpotifyAccessTokenProtected))
            settings.SpotifyAccessToken = Unprotect(settings.SpotifyAccessTokenProtected);

        settings.MigrateLegacyRoot();
        settings.NormalizeKaraokeRoot();
        return settings;
    }

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

    public void Save()
    {
        try
        {
            SpotifyClientSecretProtected = Protect(SpotifyClientSecret);
            SpotifyAccessTokenProtected = Protect(SpotifyAccessToken);
            LegacyPlaintextSecret = null; // never write plaintext back out
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
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
}

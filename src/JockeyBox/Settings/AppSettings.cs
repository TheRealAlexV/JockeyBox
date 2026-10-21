using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JockeyBox.Settings;

/// <summary>
/// User settings, persisted as JSON in %APPDATA%\JockeyBox\settings.json.
/// The Spotify client secret is encrypted at rest with DPAPI (current user
/// scope) - it never touches disk in plaintext.
/// </summary>
public class AppSettings
{
    // Library: the Music folder is the source of truth; the Karaoke folder
    // mirrors its directory/file structure with generated instrumentals.
    public string MusicDirectory { get; set; } = "";
    public string KaraokeDirectory { get; set; } = "";

    /// <summary>Legacy (pre-1.1) single root that contained \Original + \Karaoke.</summary>
    public string RootDirectory { get; set; } = "";

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

    /// <summary>Global visual offset applied to synced lyrics (positive = later).</summary>
    public double LyricsOffsetMs { get; set; }

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

        // Unprotect OAuth access token
        if (!string.IsNullOrEmpty(settings.SpotifyAccessTokenProtected))
            settings.SpotifyAccessToken = Unprotect(settings.SpotifyAccessTokenProtected);

        settings.MigrateLegacyRoot();
        return settings;
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

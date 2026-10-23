using System.Globalization;
using System.IO;
using System.Text;
using JockeyBox.Tools;
using Microsoft.VisualBasic.FileIO;

namespace JockeyBox.Library;

/// <summary>Outcome class of a media file operation.</summary>
public enum MediaOpResult
{
    /// <summary>The operation completed; the file is where the caller asked for it.</summary>
    Ok,

    /// <summary>
    /// Refused before touching anything: a path left the library roots, the file
    /// is in use, or the name/folder failed validation. <b>Nothing was changed.</b>
    /// </summary>
    Blocked,

    /// <summary>The source audio file does not exist.</summary>
    NotFound,

    /// <summary>
    /// The destination is occupied. <see cref="MediaOpOutcome.NewPath"/> holds a
    /// free alternative the caller may retry with.
    /// </summary>
    Collision,

    /// <summary>
    /// The file system failed mid-operation. Completed moves were unwound in
    /// reverse order; <see cref="MediaOpOutcome.Message"/> says whether the
    /// rollback was clean.
    /// </summary>
    Failed,
}

/// <summary>
/// Result of one <see cref="MediaFileOps"/> call. <see cref="NewPath"/> is the
/// absolute path of the audio file after a successful move or rename, and the
/// suggested free alternative for a <see cref="MediaOpResult.Collision"/>.
/// It is null for deletes, blocked calls and failures.
/// </summary>
public sealed record MediaOpOutcome(MediaOpResult Result, string? NewPath, string? Message)
{
    /// <summary>True when <see cref="Result"/> is <see cref="MediaOpResult.Ok"/>.</summary>
    public bool Succeeded => Result == MediaOpResult.Ok;

    internal static MediaOpOutcome Success(string? newPath, string message) =>
        new(MediaOpResult.Ok, newPath, message);

    internal static MediaOpOutcome Refuse(MediaOpResult result, string message, string? newPath = null) =>
        new(result, newPath, message);
}

/// <summary>
/// The destructive half of library management: delete, rename and move audio
/// files together with their sidecars.
///
/// The service owns the file system only. Persistence (library ledger, playlists,
/// lyrics cache, any index keyed by <c>TrackEntry.RelativeKey</c>) stays with the
/// caller, which supplies the optional callbacks below. Callbacks fire only
/// <i>after</i> every file move succeeded and the new audio path was verified to
/// exist, so a callback that rewrites a store can never describe a move that did
/// not happen.
///
/// Paths: every source <i>and</i> every target must resolve, after
/// <see cref="Path.GetFullPath(string)"/>, inside <c>musicRoot</c> or
/// <c>karaokeRoot</c>. The comparison is separator-aware
/// ("C:\Music2" never matches "C:\Music") and case-insensitive, matching Windows.
/// Anything else comes back as <see cref="MediaOpResult.Blocked"/> with nothing
/// touched.
///
/// Sidecars: for an audio file "Artist\Track.mp3" the service also moves
/// "Artist\Track.lrc", "Artist\Track.jpg" / ".png", and the karaoke mirror (or the
/// music original, when the source is karaoke) - the same relative key under the
/// other root, any extension - plus that mirror's own .lrc and cover. A missing
/// sidecar is normal and not an error. If any one of them fails to move, the moves
/// already completed are unwound in reverse order so a track is never left split
/// across two folders.
///
/// Collision policy is deliberately asymmetric:
/// <list type="bullet">
///   <item><see cref="Rename"/> silently picks the first free "Name (2)"...
///   "Name (999)" - a rename is expressed as one name, so a suffix is the
///   expected result of picking a taken name.</item>
///   <item><see cref="MoveTo"/> and <see cref="MoveToPreset"/> never rename
///   behind the caller's back: they return <see cref="MediaOpResult.Collision"/>
///   with a suggested free path in <see cref="MediaOpOutcome.NewPath"/> and the
///   caller decides whether to ask the user, retry with the suggestion or skip.
///   Nothing is moved in that case.</item>
/// </list>
///
/// Playback safety: every file the operation would touch is probed with an
/// exclusive read open first (<see cref="FileShare.None"/>). A file held by the
/// audio engine (or any other process) comes back as
/// <see cref="MediaOpResult.Blocked"/> with "the file is in use" instead of a
/// half-finished move.
///
/// Threading: not thread-safe. Call it from the UI thread or serialize calls; the
/// UI is expected to show a progress/disable state while a move runs.
/// </summary>
public sealed class MediaFileOps
{
    private const int MaxBaseNameLength = 120;
    private const int MaxSuffixAttempts = 999;
    private const string UnknownArtist = "Unknown Artist";

    /// <summary>Extensions that a karaoke mirror may carry (preferred over others).</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".wma", ".mp4", ".ogg", ".opus",
    };

    /// <summary>Cover art that belongs to a track by file name.</summary>
    private static readonly string[] CoverExtensions = { ".jpg", ".png" };

    /// <summary>Never valid in a Windows file or folder name.</summary>
    private static readonly char[] InvalidNameChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>Folder names may carry separators, so those are excluded here.</summary>
    private static readonly char[] InvalidFolderChars = { ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>Device names Windows reserves, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _musicRoot;
    private readonly string _karaokeRoot;

    /// <summary>
    /// Create the service for the configured library roots. Roots may be empty
    /// (a music-only or karaoke-only library); at least one must be set.
    /// </summary>
    /// <exception cref="ArgumentException">Neither root is a usable folder path.</exception>
    public MediaFileOps(string musicRoot, string karaokeRoot)
    {
        _musicRoot = NormalizeRoot(musicRoot, nameof(musicRoot));
        _karaokeRoot = NormalizeRoot(karaokeRoot, nameof(karaokeRoot));

        if (_musicRoot.Length == 0 && _karaokeRoot.Length == 0)
            throw new ArgumentException("At least one library root is required.", nameof(musicRoot));
    }

    /// <summary>The music root as a full path without a trailing separator ("" when unset).</summary>
    public string MusicRoot => _musicRoot;

    /// <summary>The karaoke root as a full path without a trailing separator ("" when unset).</summary>
    public string KaraokeRoot => _karaokeRoot;

    /// <summary>
    /// Called once per successful operation with (old absolute audio path, new
    /// absolute audio path) so the caller can rewrite a store that holds paths.
    /// Not called for <see cref="Delete"/>.
    /// </summary>
    public Action<string, string>? OnLibraryFileMoved { get; set; }

    /// <summary>
    /// Called after a successful move when the library-relative key (relative
    /// path without extension) changed: (old RelativeKey, new RelativeKey).
    /// Fires even when only the casing changed, because keys are persisted as
    /// text. Not called when the key is unchanged.
    /// </summary>
    public Action<string, string>? OnRelativeKeyChanged { get; set; }

    /// <summary>Called after any successful delete, rename or move.</summary>
    public Action? OnLibraryChanged { get; set; }

    // ---- delete ------------------------------------------------------------

    /// <summary>
    /// Send <paramref name="audioPath"/> to the Recycle Bin, together with its
    /// .lrc and cover sidecars when they exist. The karaoke mirror is left alone:
    /// it is a separate playable track, not a sidecar, and deleting the music file
    /// must not silently remove the karaoke version.
    ///
    /// Uses VB's <c>FileSystem.DeleteFile(..., SendToRecycleBin, DoNothing)</c>.
    /// Only when that throws (recycle bin disabled, a UNC path, a file too large
    /// for the bin) does it fall back to <see cref="File.Delete(string)"/>, and the
    /// returned message says which of the two happened. The audio file decides the
    /// result; sidecar deletions are best effort (a failure is logged, not
    /// reported), because a leftover .lrc is harmless while a refused delete is not.
    /// </summary>
    public MediaOpOutcome Delete(string audioPath)
    {
        if (GuardSource(audioPath, out var source, out _) is { } refusal) return refusal;

        if (IsInUse(source))
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked,
                $"Cannot delete '{source}': the file is in use.");

        var sidecars = SidecarsOf(source);
        foreach (var sidecar in sidecars)
        {
            if (IsInUse(sidecar))
                return MediaOpOutcome.Refuse(MediaOpResult.Blocked,
                    $"Cannot delete '{source}': its sidecar '{sidecar}' is in use.");
        }

        var (deleted, how) = Recycle(source);
        if (!deleted)
            return MediaOpOutcome.Refuse(MediaOpResult.Failed,
                $"Could not delete '{source}': {how}");

        var sidecarsDeleted = 0;
        foreach (var sidecar in sidecars)
        {
            var (sidecarDeleted, sidecarHow) = Recycle(sidecar);
            if (sidecarDeleted) sidecarsDeleted++;
            else Log.Warn($"Could not delete sidecar '{sidecar}': {sidecarHow}");
        }

        Notify(() => OnLibraryChanged?.Invoke(), nameof(OnLibraryChanged));

        var suffix = sidecarsDeleted > 0
            ? $" and {sidecarsDeleted.ToString(CultureInfo.InvariantCulture)} sidecar file(s)"
            : "";
        return MediaOpOutcome.Success(null, $"Deleted '{source}'{suffix} {how}.");
    }

    // ---- rename ------------------------------------------------------------

    /// <summary>
    /// Rename <paramref name="audioPath"/> inside its current folder, keeping the
    /// original extension and the user's casing.
    ///
    /// <paramref name="newBaseName"/> is a <i>base</i> name: a trailing copy of the
    /// original (or any known audio) extension is dropped first, so pasting
    /// "Track.mp3" from Explorer does not produce "Track.mp3.mp3".
    ///
    /// Invalid characters, trailing dots/spaces, reserved device names and an empty
    /// result are rejected as <see cref="MediaOpResult.Blocked"/> with a message -
    /// the name is never silently rewritten. Unlike <see cref="MoveTo"/>, a
    /// collision is resolved by suffixing: the first free "Name (2)" ...
    /// "Name (999)" wins, because a rename is expressed as a single name and the
    /// suffixed file is still the rename the user asked for. Sidecars are renamed
    /// with it.
    /// </summary>
    public MediaOpOutcome Rename(string audioPath, string newBaseName)
    {
        if (GuardSource(audioPath, out var source, out var root) is { } refusal) return refusal;

        var extension = Path.GetExtension(source);

        // Validated and used exactly as typed: a trailing space is rejected rather
        // than silently stripped, so the file name always matches the box.
        var requested = StripTypedExtension(newBaseName ?? "", extension);

        if (ValidateName(requested) is { } error)
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked, error);

        var folder = Path.GetDirectoryName(source) ?? root;
        if (string.Equals(requested, Path.GetFileNameWithoutExtension(source), StringComparison.Ordinal))
            return MediaOpOutcome.Success(source, "The file already has that name.");

        return MoveCore(source, root, folder, requested, autoSuffix: true);
    }

    // ---- move --------------------------------------------------------------

    /// <summary>
    /// Move <paramref name="audioPath"/> into <paramref name="targetFolder"/>,
    /// keeping its file name and extension.
    ///
    /// <paramref name="targetFolder"/> is relative to the root that holds the file
    /// (the music root for music files, the karaoke root for karaoke-only files);
    /// an empty value means that root itself, and an absolute path is accepted as
    /// long as it resolves inside the root. Folders are created as needed.
    ///
    /// A taken destination is <b>not</b> renamed around: the call returns
    /// <see cref="MediaOpResult.Collision"/> with the first free candidate in
    /// <see cref="MediaOpOutcome.NewPath"/> and moves nothing, so the caller can
    /// ask the user (see the class remarks on the asymmetry with
    /// <see cref="Rename"/>).
    /// </summary>
    public MediaOpOutcome MoveTo(string audioPath, string targetFolder)
    {
        if (GuardSource(audioPath, out var source, out var root) is { } refusal) return refusal;

        if (!TryResolveFolder(root, targetFolder, out var folder, out var error))
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked, error!);

        var baseName = Path.GetFileNameWithoutExtension(source);
        return MoveCore(source, root, folder, baseName, autoSuffix: false);
    }

    /// <summary>
    /// Move <paramref name="audioPath"/> into <paramref name="targetFolder"/>
    /// under <paramref name="newBaseName"/> (extension kept). This is the
    /// follow-up to a collision the user accepted: the caller passes the free
    /// name the failed <see cref="MoveTo"/> / <see cref="MoveToPreset"/> call
    /// suggested, and the file lands on exactly that path.
    ///
    /// Sidecars and the karaoke mirror move with it. The name is validated like
    /// <see cref="Rename"/>'s, but a collision is still reported rather than
    /// suffixed, so a caller that reuses a stale suggestion cannot loop.
    /// </summary>
    public MediaOpOutcome MoveToNamed(string audioPath, string targetFolder, string newBaseName)
    {
        if (GuardSource(audioPath, out var source, out var root) is { } refusal) return refusal;

        if (!TryResolveFolder(root, targetFolder, out var folder, out var error))
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked, error!);

        var requested = StripTypedExtension(newBaseName ?? "", Path.GetExtension(source));
        if (ValidateName(requested) is { } nameError)
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked, nameError);

        return MoveCore(source, root, folder, requested, autoSuffix: false);
    }

    /// <summary>
    /// Move <paramref name="audioPath"/> to the download layout's
    /// "Artist\Album\Title" shape and rename the file to the sanitised title.
    ///
    /// Blank fields follow the same policy as
    /// <c>Downloads.DownloadPaths</c>: a blank artist becomes "Unknown Artist", and
    /// a blank album drops the album folder (Artist\Title), because an untagged
    /// album would otherwise bury tracks under an "Unknown Album" folder. A blank
    /// title keeps the current file name.
    ///
    /// Preset fields are cleaned like download paths (invalid characters become
    /// "_", trailing dots/spaces are dropped, reserved names get a trailing "_")
    /// rather than rejected, because they come from tags. Collisions behave like
    /// <see cref="MoveTo"/>: <see cref="MediaOpResult.Collision"/> plus a suggestion,
    /// nothing moved.
    /// </summary>
    public MediaOpOutcome MoveToPreset(string audioPath, string artist, string album, string title)
    {
        if (GuardSource(audioPath, out var source, out var root) is { } refusal) return refusal;

        var artistSegment = CleanSegment(artist);
        if (artistSegment.Length == 0) artistSegment = UnknownArtist;

        var albumSegment = CleanSegment(album);
        var titleSegment = CleanSegment(title);
        if (titleSegment.Length == 0) titleSegment = Path.GetFileNameWithoutExtension(source);

        var relative = albumSegment.Length == 0
            ? artistSegment
            : Path.Combine(artistSegment, albumSegment);

        var folder = Path.GetFullPath(Path.Combine(root, relative));
        return MoveCore(source, root, folder, titleSegment, autoSuffix: false);
    }

    /// <summary>
    /// The folder (relative to the music root) a move would target for these tags:
    /// "Artist\Album" when both are known, "Artist" when the album is blank, and
    /// "Album" when only the album is known. With no artist and no album the file's
    /// current folder is returned, so the dialog opens on the track's own folder.
    /// Never returns an absolute path or an empty segment.
    /// </summary>
    public string SuggestFolder(string audioPath, string artist, string album)
    {
        var artistSegment = CleanSegment(artist);
        var albumSegment = CleanSegment(album);

        if (artistSegment.Length > 0)
            return albumSegment.Length > 0
                ? Path.Combine(artistSegment, albumSegment)
                : artistSegment;

        if (albumSegment.Length > 0) return albumSegment;

        try
        {
            var full = Path.GetFullPath(audioPath ?? "");
            var root = RootContaining(full);
            if (root.Length > 0)
            {
                var folder = Path.GetDirectoryName(full) ?? root;
                var relative = Path.GetRelativePath(root, folder);
                if (relative != ".") return relative;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log.Warn($"Could not suggest a folder for '{audioPath}': {ex.Message}");
        }

        return "";
    }

    /// <summary>
    /// The folder suggestion from tags alone, for the move dialog's preset button:
    /// "Artist\Album", "Artist", or "" when both are blank. Uses the same cleaning
    /// rules as <see cref="MoveToPreset"/>.
    /// </summary>
    public static string SuggestFolderFromTags(string? artist, string? album)
    {
        var artistSegment = CleanSegment(artist);
        var albumSegment = CleanSegment(album);

        if (artistSegment.Length == 0) return albumSegment;
        return albumSegment.Length == 0 ? artistSegment : Path.Combine(artistSegment, albumSegment);
    }

    // ---- validation (shared with the dialogs) ------------------------------

    /// <summary>
    /// Validate a user-supplied base name. Returns null when it is acceptable,
    /// otherwise a sentence to show inline in the rename dialog.
    ///
    /// The extension is not part of the argument: it is preserved from the file.
    /// Names are checked <i>as typed</i> - a trailing space or dot is refused
    /// rather than trimmed, so what the user sees is what the file gets.
    /// </summary>
    public static string? ValidateName(string? baseName)
    {
        var name = baseName ?? "";

        if (name.Trim().Length == 0) return "Enter a file name.";
        if (name.EndsWith(' ')) return "A file name cannot end with a space; remove it or add more text.";
        if (name.EndsWith('.')) return "A file name cannot end with a dot.";
        if (name is "." or "..") return "Enter a file name other than \".\" or \"..\".";

        if (name.IndexOfAny(InvalidNameChars) >= 0)
            return "A file name cannot contain any of: \\ / : * ? \" < > |";

        foreach (var ch in name)
        {
            if (ch < ' ')
                return "A file name cannot contain control characters.";
        }

        var stem = name.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
            return $"\"{stem}\" is reserved by Windows; choose another name.";

        if (name.Length > MaxBaseNameLength)
            return $"Keep the name under {MaxBaseNameLength} characters.";

        return null;
    }

    /// <summary>
    /// Validate a folder typed into the move dialog. The value is relative to the
    /// library, so it may be empty (the root) but not absolute or escaping.
    /// Returns null when acceptable, otherwise a sentence to show inline.
    /// </summary>
    public static string? ValidateFolder(string? folder)
    {
        var text = (folder ?? "").Trim();
        if (text.Length == 0) return null;

        if (Path.IsPathRooted(text))
            return "Enter a folder inside the library, not a full path.";

        // Both separators are accepted, so '\' and '/' are not invalid here.
        if (text.IndexOfAny(InvalidFolderChars) >= 0)
            return "A folder cannot contain any of: : * ? \" < > |";

        foreach (var ch in text)
        {
            if (ch < ' ')
                return "A folder cannot contain control characters.";
        }

        foreach (var segment in text.Split('/', '\\'))
        {
            if (segment.Length == 0) continue;
            if (segment is "." or "..")
                return "The folder must stay inside the library.";
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
                return "A folder name cannot end with a dot or a space.";
            if (ReservedNames.Contains(segment.Split('.')[0].TrimEnd(' ')))
                return $"\"{segment.Split('.')[0]}\" is reserved by Windows; choose another folder.";
        }

        return null;
    }

    // ---- core move ---------------------------------------------------------

    private MediaOpOutcome MoveCore(
        string source, string root, string destinationFolder, string baseName, bool autoSuffix)
    {
        var extension = Path.GetExtension(source);

        string folder;
        try
        {
            folder = Path.GetFullPath(destinationFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked,
                $"'{destinationFolder}' is not a valid folder path.");
        }

        if (!IsInside(root, folder))
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked,
                $"'{folder}' is outside the library; nothing was changed.");

        string finalBase;
        List<MoveStep> plan;

        if (autoSuffix)
        {
            // Rename: take the first free "Name (n)".
            finalBase = baseName;
            plan = new List<MoveStep>();
            var free = false;

            for (var n = 1; n <= MaxSuffixAttempts && !free; n++)
            {
                finalBase = n == 1
                    ? baseName
                    : $"{baseName} ({n.ToString(CultureInfo.InvariantCulture)})";
                plan = BuildPlan(source, root, folder, finalBase);
                free = TargetsFree(plan, out _);
            }

            if (!free)
            {
                var last = $"{baseName} ({MaxSuffixAttempts.ToString(CultureInfo.InvariantCulture)})";
                return MediaOpOutcome.Refuse(MediaOpResult.Collision,
                    $"No free name for '{baseName}' up to {MaxSuffixAttempts}.",
                    Path.Combine(folder, last + extension));
            }
        }
        else
        {
            // Move: never rename around a collision; suggest and let the caller decide.
            finalBase = baseName;
            plan = BuildPlan(source, root, folder, finalBase);

            if (!TargetsFree(plan, out var blocker))
                return MediaOpOutcome.Refuse(MediaOpResult.Collision,
                    $"'{blocker}' already exists; nothing was moved. " +
                    $"The first free name is '{Path.GetFileName(SuggestAlternative(source, root, folder, baseName))}'.",
                    SuggestAlternative(source, root, folder, baseName));
        }

        var newAudioPath = Path.Combine(folder, finalBase + extension);

        if (plan.Count == 0)
            return MediaOpOutcome.Success(source, "The file is already at that location.");

        foreach (var step in plan)
        {
            if (!IsInsideAnyRoot(step.To))
                return MediaOpOutcome.Refuse(MediaOpResult.Blocked,
                    $"'{step.To}' is outside the library; nothing was changed.");
        }

        foreach (var step in plan)
        {
            if (IsInUse(step.From))
                return MediaOpOutcome.Refuse(MediaOpResult.Blocked,
                    $"Cannot move '{Path.GetFileName(step.From)}': the file is in use.");
        }

        try
        {
            foreach (var step in plan)
            {
                var directory = Path.GetDirectoryName(step.To);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not create the destination folder for '{source}'", ex);
            return MediaOpOutcome.Refuse(MediaOpResult.Failed,
                $"Could not create '{folder}': {ex.Message} Nothing was changed.");
        }

        var completed = new List<MoveStep>(plan.Count);
        try
        {
            foreach (var step in plan)
            {
                File.Move(step.From, step.To);
                completed.Add(step);
            }

            if (!File.Exists(newAudioPath))
                throw new IOException($"'{newAudioPath}' does not exist after the move.");
        }
        catch (Exception ex)
        {
            var rolledBack = Rollback(completed);
            Log.Error($"Move failed: '{source}' -> '{newAudioPath}'", ex);

            return MediaOpOutcome.Refuse(MediaOpResult.Failed,
                $"Moving '{Path.GetFileName(source)}' failed: {ex.Message} " +
                (rolledBack
                    ? "Nothing was changed."
                    : "Some files may be left at the new location; check both folders."));
        }

        // Files are in place and verified: only now do the caller's stores hear about it.
        var oldKey = RelativeKey(root, source);
        var newKey = RelativeKey(root, newAudioPath);

        Notify(() => OnLibraryFileMoved?.Invoke(source, newAudioPath), nameof(OnLibraryFileMoved));
        if (!string.Equals(oldKey, newKey, StringComparison.Ordinal))
        {
            Notify(() => OnRelativeKeyChanged?.Invoke(oldKey, newKey), nameof(OnRelativeKeyChanged));
        }
        Notify(() => OnLibraryChanged?.Invoke(), nameof(OnLibraryChanged));

        var movedSidecars = plan.Count - 1;
        var sidecarNote = movedSidecars > 0
            ? $" and {movedSidecars.ToString(CultureInfo.InvariantCulture)} sidecar file(s)"
            : "";

        return MediaOpOutcome.Success(newAudioPath,
            $"Moved '{Path.GetFileName(source)}'{sidecarNote} to '{newAudioPath}'.");
    }

    /// <summary>
    /// The files a move carries: the audio file, its .lrc and covers, and the
    /// karaoke counterpart (with its own .lrc and covers). No-ops are dropped so a
    /// same-folder operation cannot call File.Move onto itself.
    /// </summary>
    private List<MoveStep> BuildPlan(string source, string root, string destinationFolder, string newBaseName)
    {
        var steps = new List<MoveStep>(6);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddStep(string from, string to)
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
            if (!seen.Add(to)) return;
            steps.Add(new MoveStep(from, to));
        }

        var extension = Path.GetExtension(source);
        AddStep(source, Path.Combine(destinationFolder, newBaseName + extension));

        var sourceFolder = Path.GetDirectoryName(source) ?? root;
        var sourceBase = Path.GetFileNameWithoutExtension(source);
        AddSidecars(AddStep, sourceFolder, sourceBase, destinationFolder, newBaseName);

        var otherRoot = OtherRoot(root);
        if (otherRoot.Length == 0) return steps;

        var mirror = FindMirror(source, root, otherRoot);
        if (mirror is null) return steps;

        var mirrorFolder = Path.GetDirectoryName(mirror) ?? otherRoot;
        var mirrorBase = Path.GetFileNameWithoutExtension(mirror);

        var relativeFolder = Path.GetRelativePath(root, destinationFolder);
        var mirrorDestination = relativeFolder == "."
            ? otherRoot
            : Path.GetFullPath(Path.Combine(otherRoot, relativeFolder));

        AddStep(mirror, Path.Combine(mirrorDestination, newBaseName + Path.GetExtension(mirror)));
        AddSidecars(AddStep, mirrorFolder, mirrorBase, mirrorDestination, newBaseName);

        return steps;
    }

    private static void AddSidecars(
        Action<string, string> addStep,
        string sourceFolder, string sourceBase,
        string destinationFolder, string destinationBase)
    {
        var lyrics = Path.Combine(sourceFolder, sourceBase + ".lrc");
        if (File.Exists(lyrics))
            addStep(lyrics, Path.Combine(destinationFolder, destinationBase + ".lrc"));

        foreach (var cover in CoverExtensions)
        {
            var path = Path.Combine(sourceFolder, sourceBase + cover);
            if (File.Exists(path))
                addStep(path, Path.Combine(destinationFolder, destinationBase + cover));
        }
    }

    /// <summary>
    /// First free alternative for <paramref name="baseName"/> in
    /// <paramref name="folder"/>, "Name (2)" upwards, considering sidecars too.
    /// </summary>
    private string SuggestAlternative(string source, string root, string folder, string baseName)
    {
        for (var n = 2; n <= MaxSuffixAttempts; n++)
        {
            var candidate = $"{baseName} ({n.ToString(CultureInfo.InvariantCulture)})";
            var plan = BuildPlan(source, root, folder, candidate);
            if (TargetsFree(plan, out _))
                return Path.Combine(folder, candidate + Path.GetExtension(source));
        }

        return Path.Combine(folder,
            $"{baseName} ({MaxSuffixAttempts.ToString(CultureInfo.InvariantCulture)})" +
            Path.GetExtension(source));
    }

    // ---- sidecar discovery -------------------------------------------------

    /// <summary>The .lrc and cover files that sit next to <paramref name="audioPath"/>.</summary>
    private static List<string> SidecarsOf(string audioPath)
    {
        var folder = Path.GetDirectoryName(audioPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        var sidecars = new List<string>(3);

        var lyrics = Path.Combine(folder, baseName + ".lrc");
        if (File.Exists(lyrics)) sidecars.Add(lyrics);

        foreach (var cover in CoverExtensions)
        {
            var path = Path.Combine(folder, baseName + cover);
            if (File.Exists(path)) sidecars.Add(path);
        }

        return sidecars;
    }

    /// <summary>
    /// The karaoke counterpart (or music original) of <paramref name="source"/>:
    /// a file in the same relative folder under the other root with the same base
    /// name and any audio extension. Sidecar extensions are ignored.
    /// </summary>
    private string? FindMirror(string source, string root, string otherRoot)
    {
        try
        {
            var relative = Path.GetRelativePath(root, source);
            var relativeFolder = Path.GetDirectoryName(relative) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(relative);
            var folder = Path.Combine(otherRoot, relativeFolder);

            if (!Directory.Exists(folder)) return null;

            string? fallback = null;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (string.Equals(file, source, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Path.GetFileNameWithoutExtension(file), baseName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var extension = Path.GetExtension(file);
                if (extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase)) continue;
                if (CoverExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;

                // Prefer a real audio file over whatever else shares the name.
                if (AudioExtensions.Contains(extension)) return file;
                fallback ??= file;
            }

            return fallback;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not look up the karaoke counterpart of '{source}': {ex.Message}");
            return null;
        }
    }

    // ---- guards ------------------------------------------------------------

    /// <summary>
    /// Validate the audio file being acted on. Returns null with
    /// <paramref name="fullPath"/> / <paramref name="root"/> filled in, or the
    /// refusal to return to the caller.
    /// </summary>
    private MediaOpOutcome? GuardSource(string? audioPath, out string fullPath, out string root)
    {
        fullPath = "";
        root = "";

        if (string.IsNullOrWhiteSpace(audioPath))
            return MediaOpOutcome.Refuse(MediaOpResult.NotFound, "No file was given.");

        try
        {
            fullPath = Path.GetFullPath(audioPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked,
                $"'{audioPath}' is not a valid file path.");
        }

        root = RootContaining(fullPath);
        if (root.Length == 0)
            return MediaOpOutcome.Refuse(MediaOpResult.Blocked,
                $"'{fullPath}' is outside the music and karaoke folders; nothing was changed.");

        if (!File.Exists(fullPath))
            return MediaOpOutcome.Refuse(MediaOpResult.NotFound, $"'{fullPath}' does not exist.");

        return null;
    }

    /// <summary>
    /// Resolve a caller-supplied destination folder against <paramref name="root"/>.
    /// Empty means the root itself; a rooted path is accepted only when it stays
    /// inside the root.
    /// </summary>
    private static bool TryResolveFolder(
        string root, string? folderText, out string folder, out string? error)
    {
        folder = root;
        error = null;

        var text = (folderText ?? "").Trim();
        if (text.Length == 0) return true;

        if (Path.IsPathRooted(text))
        {
            try
            {
                folder = Path.GetFullPath(text);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"'{text}' is not a valid folder path.";
                return false;
            }

            if (!IsInside(root, folder))
            {
                error = $"'{text}' is outside the library; nothing was changed.";
                return false;
            }

            return true;
        }

        if (ValidateFolder(text) is { } message)
        {
            error = message;
            return false;
        }

        folder = Path.GetFullPath(Path.Combine(root, text));
        if (!IsInside(root, folder))
        {
            error = $"'{text}' is outside the library; nothing was changed.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is <paramref name="root"/> or sits
    /// below it. Separator-aware, so "C:\Music2" never matches "C:\Music".
    /// </summary>
    private static bool IsInside(string root, string fullPath)
    {
        if (root.Length == 0 || fullPath.Length == 0) return false;

        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return false;

        if (string.Equals(fullPath, trimmed, StringComparison.OrdinalIgnoreCase)) return true;

        return fullPath.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(trimmed + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsInsideAnyRoot(string fullPath) =>
        IsInside(_musicRoot, fullPath) || IsInside(_karaokeRoot, fullPath);

    /// <summary>
    /// The most specific configured root that contains <paramref name="fullPath"/>,
    /// or "" when the path is outside the library. Longest match first, so a karaoke
    /// folder nested inside the music folder still resolves to the karaoke root.
    /// </summary>
    private string RootContaining(string fullPath)
    {
        var match = "";
        if (IsInside(_musicRoot, fullPath) && _musicRoot.Length > match.Length) match = _musicRoot;
        if (IsInside(_karaokeRoot, fullPath) && _karaokeRoot.Length > match.Length) match = _karaokeRoot;
        return match;
    }

    /// <summary>The opposite root, or "" when the other root is unset or the same folder.</summary>
    private string OtherRoot(string root)
    {
        if (_musicRoot.Length == 0 || _karaokeRoot.Length == 0) return "";
        if (string.Equals(_musicRoot, _karaokeRoot, StringComparison.OrdinalIgnoreCase)) return "";

        if (string.Equals(root, _musicRoot, StringComparison.OrdinalIgnoreCase)) return _karaokeRoot;
        if (string.Equals(root, _karaokeRoot, StringComparison.OrdinalIgnoreCase)) return _musicRoot;
        return "";
    }

    /// <summary>
    /// Playback guard: a file is treated as in use unless an exclusive read handle
    /// can be taken. A missing file is not locked (the caller reports NotFound).
    /// </summary>
    private static bool IsInUse(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only permission or a share that forbids reading: assume in use.
            return true;
        }
    }

    /// <summary>Reverse-order unwind of the moves that already happened.</summary>
    private static bool Rollback(List<MoveStep> completed)
    {
        var clean = true;
        for (var i = completed.Count - 1; i >= 0; i--)
        {
            var step = completed[i];
            try
            {
                if (File.Exists(step.To)) File.Move(step.To, step.From);
            }
            catch (Exception ex)
            {
                clean = false;
                Log.Error($"Rollback failed: '{step.To}' -> '{step.From}'", ex);
            }
        }

        return clean;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Send one file to the Recycle Bin, falling back to a permanent delete when
    /// the recycle call throws. Returns whether the file is gone and a phrase that
    /// says which route was taken.
    /// </summary>
    private static (bool Deleted, string How) Recycle(string path)
    {
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin, UICancelOption.DoNothing);
            return (true, "to the Recycle Bin");
        }
        catch (Exception ex)
        {
            Log.Warn($"Recycle Bin delete failed for '{path}', falling back to File.Delete: " +
                     $"{ex.GetType().Name}: {ex.Message}");

            try
            {
                File.Delete(path);
                return (true, $"permanently (the Recycle Bin was unavailable: {ex.Message})");
            }
            catch (Exception inner)
            {
                Log.Error($"File.Delete failed for '{path}'", inner);
                return (false, inner.Message);
            }
        }
    }

    /// <summary>Library-relative key: relative path without its extension.</summary>
    private static string RelativeKey(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var folder = Path.GetDirectoryName(relative) ?? "";
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(relative));
    }

    /// <summary>
    /// Clean one folder/file segment the way the download layout does: invalid
    /// characters become "_", surrounding whitespace and trailing dots go, reserved
    /// device names get a trailing "_", and "."/".." collapse to "".
    /// </summary>
    private static string CleanSegment(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch < ' ' || Array.IndexOf(InvalidNameChars, ch) >= 0 ? '_' : ch);
        }

        var clean = sb.ToString().Trim().TrimEnd('.');

        if (clean is "." or "..") return "";
        if (ReservedNames.Contains(clean.Split('.')[0].TrimEnd(' '))) clean += "_";
        if (clean.Length > MaxBaseNameLength) clean = clean[..MaxBaseNameLength].TrimEnd().TrimEnd('.');
        return clean;
    }

    /// <summary>
    /// Drop a trailing extension the user typed into the rename box when it is the
    /// file's own extension or another known audio extension, so the preserved
    /// extension is not doubled.
    /// </summary>
    private static string StripTypedExtension(string name, string extension)
    {
        if (name.Length == 0) return name;

        var typed = Path.GetExtension(name);
        if (typed.Length == 0) return name;

        var isOwn = extension.Length > 0 && typed.Equals(extension, StringComparison.OrdinalIgnoreCase);
        if (!isOwn && !AudioExtensions.Contains(typed)) return name;

        return name[..^typed.Length].TrimEnd();
    }

    private static string NormalizeRoot(string? root, string parameterName)
    {
        var text = (root ?? "").Trim();
        if (text.Length == 0) return "";

        try
        {
            return Path.GetFullPath(text)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"'{root}' is not a valid folder path.", parameterName, ex);
        }
    }

    /// <summary>
    /// True when no destination is taken and no two steps target the same path.
    /// A step whose target is its own source (ignoring case, so a case-only rename
    /// is allowed) is the same file and cannot collide.
    /// </summary>
    private static bool TargetsFree(List<MoveStep> plan, out string? blocker)
    {
        blocker = null;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in plan)
        {
            if (!targets.Add(step.To))
            {
                blocker = step.To;
                return false;
            }

            if (string.Equals(step.From, step.To, StringComparison.OrdinalIgnoreCase)) continue;

            if (File.Exists(step.To) || Directory.Exists(step.To))
            {
                blocker = step.To;
                return false;
            }
        }

        return true;
    }

    /// <summary>Notify helpers must never turn a completed file move into a crash.</summary>
    private static void Notify(Action action, string name)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"{name} threw while reporting a media file operation: " +
                     $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>One file move: from absolute source to absolute destination.</summary>
    private sealed record MoveStep(string From, string To);
}

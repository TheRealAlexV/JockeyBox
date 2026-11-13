using System.IO;
using JockeyBox.Separation;

namespace JockeyBox.Library;

/// <summary>
/// The one naming rule for the merged single-root library: the karaoke version of
/// "Artist\Album\Song.mp3" is "Artist\Album\Song.karaoke.mp3".
///
/// The marker sits between the base name and the extension, so the pair shares
/// one folder and the two files may still use different formats (a generated
/// instrumental is often .flac next to a downloaded .mp3). Every place that
/// writes, pairs or looks up a karaoke file goes through these helpers, so the
/// scanner, the separation output path and the legacy mirror mapping cannot
/// disagree about where a partner lives.
/// </summary>
public static class KaraokeNaming
{
    /// <summary>Marker inserted before the extension: "Song" + Marker + ".mp3".</summary>
    public const string Marker = ".karaoke";

    /// <summary>
    /// Marker for a reverse-karaoke render: "Song" + VocalsMarker + ".mp3". It is
    /// deliberately not the karaoke marker, so the vocals take of a song is its
    /// own row and never overwrites (or pairs as) the instrumental.
    /// </summary>
    public const string VocalsMarker = ".vocals";

    /// <summary>
    /// Marker for a keep-backing render ("Song" + BackingMarker + ".mp3"). It is
    /// separate from <see cref="Marker"/> because the two modes are two different
    /// files: a plain instrumental and a keep-backing one used to share
    /// "Song.karaoke.mp3", so the second render silently overwrote the first and
    /// no library could hold both. Files written before this split keep the
    /// karaoke name - nothing is renamed, so an old track simply has one
    /// instrumental entry instead of two.
    /// </summary>
    public const string BackingMarker = ".backing";

    /// <summary>
    /// Every karaoke render is MP3, whatever the source container was: the
    /// separation step emits WAV and the encode step writes the final file.
    /// </summary>
    public const string RenderExtension = ".mp3";

    /// <summary>
    /// The one owner of (stem mode, source path) → rendered file path. Each mode
    /// has its own name: "Song.karaoke.mp3" for a plain instrumental,
    /// "Song.backing.mp3" for the keep-backing variant and "Song.vocals.mp3" for
    /// the reverse render, so one track can hold all three at once. Returns ""
    /// when the source has no folder or no base name.
    /// </summary>
    public static string RenderPathFor(KaraokeStemMode mode, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return "";

        var stem = BaseFileName(sourcePath);
        if (stem.Length == 0) return "";

        var folder = Path.GetDirectoryName(sourcePath);
        var name = RenderName(mode, stem);
        return string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name);
    }

    /// <summary>
    /// File name of a render: the base name plus the mode's marker and ".mp3".
    /// A relative key ("Artist\Album\Song") uses this too, so the legacy mirror
    /// and the beside-the-source path cannot disagree.
    /// </summary>
    public static string RenderName(KaraokeStemMode mode, string? baseName)
    {
        if (string.IsNullOrEmpty(baseName)) return "";
        return baseName + MarkerOf(mode) + RenderExtension;
    }

    /// <summary>The marker a mode writes, so the name rule lives in one place.</summary>
    public static string MarkerOf(KaraokeStemMode mode) => mode switch
    {
        KaraokeStemMode.VocalsOnly => VocalsMarker,
        KaraokeStemMode.InstrumentalKeepBacking => BackingMarker,
        _ => Marker,
    };

    /// <summary>
    /// True when the file name already carries the marker ("Song.karaoke.flac").
    /// A bare ".karaoke.flac" (empty base name) is not a pair, so it keeps a
    /// library row of its own instead of pairing with nothing.
    /// </summary>
    public static bool IsKaraokeFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        return IsKaraokeStem(StemOf(Path.GetFileName(fileName)));
    }

    /// <summary>
    /// Base name of a file name: "Song.karaoke.mp3" and "Song.mp3" both give
    /// "Song". This is what <c>TrackEntry.RelativeKey</c> is built from, so a
    /// karaoke file never becomes a library row of its own when its original
    /// exists.
    /// </summary>
    public static string BaseFileName(string? fileName)
    {
        var stem = StemOf(Path.GetFileName(fileName));
        var marker = MarkerIn(stem);
        return marker.Length == 0 ? stem : stem[..^marker.Length];
    }

    /// <summary>
    /// Karaoke file name of a file name: "Song.mp3" becomes "Song.karaoke.mp3".
    /// A name that already carries the marker comes back unchanged, so calling
    /// this twice is safe.
    /// </summary>
    public static string KaraokeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName) ?? "";
        var extension = Path.GetExtension(name);
        var stem = StemOf(name);

        if (stem.Length == 0) return name;    // ".mp3" has no base name to mark
        if (IsKaraokeStem(stem)) return name; // already "Song.karaoke.mp3"

        return stem + Marker + extension;
    }

    /// <summary>
    /// The karaoke partner path of an audio file: same folder, marked name, same
    /// extension. Callers that need another format (a generated .flac beside a
    /// downloaded .mp3) replace the extension themselves.
    /// </summary>
    public static string KaraokePathFor(string? audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath)) return "";

        var name = KaraokeFileName(audioPath);
        var folder = Path.GetDirectoryName(audioPath);
        return string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name);
    }

    /// <summary>
    /// Insert <paramref name="suffix"/> into the base name, before the marker:
    /// "Song.mp3" with " (2)" gives "Song (2).mp3" and "Song.karaoke.mp3" gives
    /// "Song (2).karaoke.mp3". A collision copy therefore stays a partner of the
    /// same track instead of looking like the instrumental of "Song (2)".
    /// </summary>
    public static string InsertInBaseName(string? fileName, string suffix)
    {
        var name = Path.GetFileName(fileName) ?? "";
        if (name.Length == 0 || string.IsNullOrEmpty(suffix)) return name;

        var stem = StemOf(name);
        if (stem.Length == 0) return name;    // ".mp3" has no base name to extend

        var extension = Path.GetExtension(name);
        var marker = MarkerIn(stem);
        return marker.Length == 0
            ? stem + suffix + extension
            : stem[..^marker.Length] + suffix + marker + extension;
    }

    /// <summary>
    /// Which renders of a track actually exist on disk, in a fixed menu order:
    /// <see cref="AudioVariant.Original"/>, <see cref="AudioVariant.Karaoke"/>,
    /// <see cref="AudioVariant.Backing"/>, <see cref="AudioVariant.Vocals"/>.
    ///
    /// <para><b>Why probe instead of assume</b>: after the split an old track has
    /// at most one instrumental render (its pre-split file still carries the
    /// karaoke name) while a new one may have both, and vocals are optional. A
    /// submenu built from this list offers only entries that will play.</para>
    ///
    /// <para>Renders are MP3 by rule (<see cref="RenderExtension"/>), but a
    /// 1.5.x library can hold an .flac beside a downloaded .mp3, so both are
    /// probed. Matching is case-insensitive on the marker only; the file system
    /// decides the rest.</para>
    /// </summary>
    public static IReadOnlyList<AudioVariant> DiscoverVariants(string? primaryAudioPath)
    {
        var found = new List<AudioVariant>(4);
        if (string.IsNullOrWhiteSpace(primaryAudioPath)) return found;

        if (File.Exists(primaryAudioPath)) found.Add(AudioVariant.Original);

        var folder = Path.GetDirectoryName(primaryAudioPath) ?? "";
        var stem = BaseFileName(primaryAudioPath);
        if (folder.Length == 0 || stem.Length == 0) return found;

        if (VariantExists(folder, stem, Marker)) found.Add(AudioVariant.Karaoke);
        if (VariantExists(folder, stem, BackingMarker)) found.Add(AudioVariant.Backing);
        if (VariantExists(folder, stem, VocalsMarker)) found.Add(AudioVariant.Vocals);

        return found;
    }

    private static bool VariantExists(string folder, string stem, string marker) =>
        VariantFile(folder, stem, marker) != null;

    /// <summary>
    /// The file one marker resolves to, or null. <see cref="DiscoverVariants"/>
    /// and <see cref="VariantPathFor"/> both come through here, so the list a
    /// caller is shown and the path it plays can never name different files.
    /// </summary>
    private static string? VariantFile(string folder, string stem, string marker)
    {
        var mp3 = Path.Combine(folder, stem + marker + RenderExtension);
        if (File.Exists(mp3)) return mp3;
        var flac = Path.Combine(folder, stem + marker + ".flac");
        return File.Exists(flac) ? flac : null;
    }

    /// <summary>
    /// Absolute path of one variant that exists on disk, or null. This is the
    /// resolver behind <see cref="DiscoverVariants"/>: a caller that has already
    /// seen a variant in that list asks for the file it describes, instead of
    /// guessing from a single partner path. Note the deck's stem readers only
    /// hold one instrumental at a time, so "Karaoke" and "Backing" can be two
    /// distinct files that a caller must switch between explicitly.
    /// </summary>
    public static string? VariantPathFor(string? primaryAudioPath, AudioVariant variant)
    {
        if (string.IsNullOrWhiteSpace(primaryAudioPath)) return null;

        if (variant == AudioVariant.Original)
            return File.Exists(primaryAudioPath) ? primaryAudioPath : null;

        var folder = Path.GetDirectoryName(primaryAudioPath) ?? "";
        var stem = BaseFileName(primaryAudioPath);
        if (folder.Length == 0 || stem.Length == 0) return null;

        return VariantFile(folder, stem, MarkerOf(variant));
    }

    /// <summary>The marker a playback variant lives under ("" for the source file).</summary>
    public static string MarkerOf(AudioVariant variant) => variant switch
    {
        AudioVariant.Karaoke => Marker,
        AudioVariant.Backing => BackingMarker,
        AudioVariant.Vocals => VocalsMarker,
        _ => "",
    };

    /// <summary>The operator-facing name of a variant; menus and rows both use it.</summary>
    public static string DisplayName(AudioVariant variant) => variant switch
    {
        AudioVariant.Karaoke => "Karaoke",
        AudioVariant.Backing => "Keep backing",
        AudioVariant.Vocals => "Vocals",
        _ => "Original",
    };

    private static string StemOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "";
        return Path.GetFileNameWithoutExtension(fileName) ?? "";
    }

    private static bool IsKaraokeStem(string stem) => MarkerIn(stem).Length > 0;

    /// <summary>
    /// The karaoke marker a stem already carries, or "" for a plain file. Both
    /// instrumental variants count: "Song.backing.mp3" is as much a render as
    /// "Song.karaoke.mp3", so it must not become a library row of its own.
    /// </summary>
    private static string MarkerIn(string stem)
    {
        if (stem.Length <= Marker.Length) return "";
        if (stem.EndsWith(Marker, StringComparison.OrdinalIgnoreCase)) return Marker;
        if (stem.EndsWith(BackingMarker, StringComparison.OrdinalIgnoreCase)) return BackingMarker;
        return "";
    }
}

/// <summary>
/// A playable take of one track, as an audio-variant submenu lists it. The order
/// of the members is the order <see cref="KaraokeNaming.DiscoverVariants"/>
/// reports: original first, then each render.
/// </summary>
public enum AudioVariant
{
    Original,
    Karaoke,
    Backing,
    Vocals,
}


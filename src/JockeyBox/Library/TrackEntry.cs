using CommunityToolkit.Mvvm.ComponentModel;

namespace JockeyBox.Library;

public enum SeparationState { None, Queued, Running, Failed }

/// <summary>
/// One logical track in the unified browser list: the music file plus, when it
/// exists, its karaoke partner.
///
/// In the merged one-root library the partner sits beside the original and is
/// named "Song.karaoke.ext" (see <see cref="KaraokeNaming"/>); in a split library
/// it mirrors the relative path under the karaoke root. Either way the pair is
/// matched on the base relative path - the extension and the marker are ignored
/// when pairing - so a row keeps one identity across the migration.
/// Observable so the browser row updates live while karaoke tracks are
/// generated in the background.
/// </summary>
public partial class TrackEntry : ObservableObject
{
    /// <summary>
    /// Pairing key: relative path with the extension and any ".karaoke" marker
    /// stripped (case-insensitive matching). The instrumental of "a/b/song.mp3"
    /// carries the key "a/b/song" as well, so playlists, the queue and settings
    /// never see a second identity for one song.
    /// </summary>
    public required string RelativeKey { get; init; }

    /// <summary>Library-relative path (for display): the music file when present, otherwise the karaoke file.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute path of the full music file (Deck B), if present.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus), nameof(NeedsKaraoke))]
    private string? _musicPath;

    /// <summary>
    /// Absolute path of the instrumental file (Deck A), if present: either the
    /// merged-layout sibling "Song.karaoke.ext" or, for an unmerged library, the
    /// copy under the karaoke root.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus), nameof(NeedsKaraoke))]
    private string? _karaokePath;

    /// <summary>Background vocal-removal state for this track.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus), nameof(NeedsKaraoke))]
    private SeparationState _separation = SeparationState.None;

    /// <summary>Progress 0-100 while Separation == Running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KaraokeStatus))]
    private int _separationProgress;

    // Metadata (read from the Music file - it carries the cleanest tags for
    // lyrics lookup - otherwise from the Karaoke file).
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    /// <summary>
    /// Musical key as the file's tag states it ("8A", "Am", "F#m"), or the
    /// analysed estimate when the file has no key tag. Null when neither exists,
    /// so the row shows its em-dash fallback rather than a guessed key.
    /// Observable because analysis often finishes after the row is on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText))]
    private string? _key;

    /// <summary>
    /// True when <see cref="Key"/> is the analyser's chroma estimate rather than
    /// the file's own TKEY tag. Observable with <see cref="Key"/>: a row analysed
    /// after it was on screen gains an estimate, not an authored key, and has to
    /// say so. Sorting ignores this - <see cref="ViewModels.TrackSortComparer"/>
    /// orders on <see cref="Key"/> itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText))]
    private bool _keyEstimated;

    /// <summary>Key column text: see <see cref="KeyNotation.Display"/> ("8A", "~8A", or the em dash).</summary>
    public string KeyText => KeyNotation.Display(Key, KeyEstimated);

    /// <summary>
    /// Tempo as the file's TBPM tag states it, or the analysed estimate when the
    /// file has no tempo tag. 0 when neither exists, so the row shows its em-dash
    /// fallback rather than a guessed tempo. Observable for the same reason as
    /// <see cref="Key"/>: analysis often finishes after the row is on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmText))]
    private double _bpm;

    public TimeSpan Duration { get; set; }

    public bool HasBoth => KaraokePath != null && MusicPath != null;

    /// <summary>
    /// True when a karaoke version still needs to be generated. False for a
    /// derived vocals-only row: that file is the output of a separation and a
    /// derived track is not a source to render karaoke from.
    /// </summary>
    public bool NeedsKaraoke => MusicPath != null && KaraokePath == null
                                && Separation is SeparationState.None or SeparationState.Failed
                                && !DerivedVocals.IsVocalsFile(MusicPath);

    /// <summary>Browser icon: generation state or pairing status.</summary>
    public string KaraokeStatus => Separation switch
    {
        SeparationState.Queued => "\u23F3 queued",
        SeparationState.Running => $"\u2699 {SeparationProgress}%",
        SeparationState.Failed => "\u2716 failed",
        _ when HasBoth => "\u2713",
        // A derived vocals row is not an offer to render anything: it has no
        // karaoke state of its own to show (Library/DerivedVocals).
        _ when DerivedVocals.IsVocalsFile(MusicPath) => "",
        _ when KaraokePath == null => "\u2b55 needed",
        _ => "no music",
    };

    /// <summary>
    /// Which takes this track has, read from disk on access through
    /// <see cref="KaraokeNaming.DiscoverVariants"/>. No cache on purpose: a render
    /// that lands while the row is on screen must show up, and a second copy of
    /// the file list is exactly the kind of state that drifts out of date.
    /// </summary>
    public VariantAvailability Variants => VariantAvailability.From(MusicPath);

    /// <summary>
    /// Raise the variant properties after a render lands. Callers on the
    /// separation path own no part of the model, so they ring this one bell
    /// instead of writing a path and hoping a dependent property was notified.
    /// </summary>
    public void NotifyVariantsChanged()
    {
        OnPropertyChanged(nameof(Variants));
        OnPropertyChanged(nameof(HasBoth));
        OnPropertyChanged(nameof(NeedsKaraoke));
        OnPropertyChanged(nameof(KaraokeStatus));
    }

    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string BpmText => Bpm > 0 ? Bpm.ToString("0.#") : "";
}

/// <summary>
/// One take of a track, the way a menu or a row wants to show it: the variant,
/// the operator-facing label, whether its file is on disk, and the glyph that
/// stands for it.
///
/// <para><b>Icon is a string, not art.</b> <see cref="VariantGlyphs"/> owns the
/// four characters. The design lane replaces those constants (and may add a
/// separate "absent" glyph); nothing else has to change, because every menu item
/// and row badge binds <see cref="Icon"/>.</para>
/// </summary>
public sealed record VariantSlot(AudioVariant Variant, string Label, string Icon, bool Exists);

/// <summary>
/// The variant ledger for one track, in the stable menu order Original,
/// Karaoke, Backing, Vocals. Built from disk every time it is asked for
/// (<see cref="KaraokeNaming.DiscoverVariants"/>), so a render that lands while
/// the row is on screen appears without anyone having to invalidate a cache.
///
/// <para>The deck's stem readers hold one instrumental at a time. Use
/// <see cref="All"/> with its <see cref="VariantSlot.Exists"/> flag for a menu
/// that keeps a fixed order and disables what is missing, and
/// <see cref="Present"/> for a row badge strip that must not draw empty slots.</para>
/// </summary>
public sealed class VariantAvailability
{
    /// <summary>A track with no file: every slot absent.</summary>
    public static VariantAvailability None { get; } = new(Array.Empty<VariantSlot>(), null);

    private readonly IReadOnlyList<VariantSlot> _all;
    private readonly IReadOnlyList<VariantSlot> _present;
    private readonly string? _source;

    private VariantAvailability(IReadOnlyList<VariantSlot> all, string? source)
    {
        _all = all;
        _present = all.Where(slot => slot.Exists).ToArray();
        _source = source;
    }

    /// <summary>Every variant in stable order, absent ones included and flagged.</summary>
    public IReadOnlyList<VariantSlot> All => _all;

    /// <summary>Only the variants whose file exists, for a badge strip.</summary>
    public IReadOnlyList<VariantSlot> Present => _present;

    /// <summary>How many variants this track actually has.</summary>
    public int Count => _present.Count;

    public bool Has(AudioVariant variant) =>
        _all.Any(slot => slot.Variant == variant && slot.Exists);

    /// <summary>The file for one variant, or null when it is not on disk.</summary>
    public string? PathFor(AudioVariant variant) =>
        Has(variant) ? KaraokeNaming.VariantPathFor(_source, variant) : null;

    /// <summary>Read the ledger for a track's source file.</summary>
    public static VariantAvailability From(string? musicPath)
    {
        if (string.IsNullOrWhiteSpace(musicPath)) return None;

        var present = KaraokeNaming.DiscoverVariants(musicPath);
        var all = new List<VariantSlot>(4);
        foreach (var variant in Enum.GetValues<AudioVariant>())
        {
            var exists = present.Contains(variant);
            all.Add(new VariantSlot(
                variant,
                KaraokeNaming.DisplayName(variant),
                exists ? VariantGlyphs.For(variant) : "",
                exists));
        }

        return new VariantAvailability(all, musicPath);
    }
}

/// <summary>
/// The characters that stand for each variant. Placeholders on purpose: the
/// design lane owns the visual vocabulary and replaces these four values (and
/// the row layout that binds them) without touching the model or the menus.
/// </summary>
public static class VariantGlyphs
{
    public const string Original = "\u25A0"; // the row's existing local-file glyph
    public const string Karaoke = "\u25C6";  // the row's existing karaoke-ready glyph
    public const string Backing = "\u25C7";
    public const string Vocals = "\u25CB";

    public static string For(AudioVariant variant) => variant switch
    {
        AudioVariant.Karaoke => Karaoke,
        AudioVariant.Backing => Backing,
        AudioVariant.Vocals => Vocals,
        _ => Original,
    };
}

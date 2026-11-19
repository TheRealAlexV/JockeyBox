using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Audio;
using JockeyBox.Audio.Graph;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.Tools;

namespace JockeyBox.ViewModels;

/// <summary>
/// The karaoke pair: one song loaded onto both decks at once, the instrumental on
/// A and the original on B, so the operator can hand over between them whenever.
/// This is a per-playback action, not a mode: it holds a lock that only an
/// ordinary load (or a stop with nothing loaded) releases, and both decks keep
/// ordinary single-file loads, so nothing here touches the split render.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// True while a karaoke pair is loaded. Public so the pair cue strip can bind
    /// its visibility to it: <c>IsABMode &amp;&amp; IsDeckSync</c> is also true for
    /// any manual A-B link, and either can be toggled mid-pair.
    /// </summary>
    [ObservableProperty]
    private bool _isKaraokePairLoaded;

    /// <summary>
    /// The pair cue bank, or null when no pair is loaded. Captures from whichever
    /// deck is master and seeks both channels directly; see
    /// <see cref="PairCueBankViewModel"/> for why the transport is bypassed.
    /// </summary>
    [ObservableProperty]
    private PairCueBankViewModel? _pairCues;

    /// <summary>The deck a pair action captures from: the active one, A by default.</summary>
    private DeckViewModel MasterDeck => _activeDeck == PlaybackDeck.B ? DeckB : DeckA;
    private bool _pairStartLogged; // the settled both-decks line, written once per pair

    /// <summary>
    /// Load the pair: one take of the song on A, the original on B. The take is
    /// the variant the operator picked; both decks get ordinary single-file
    /// loads, so the second <c>SetTrack</c> argument is always null.
    ///
    /// <para><b>Why both decks start and the link is engaged here.</b> The old
    /// body called <c>PlayDeck(PlaybackDeck.A)</c>, which names one deck and
    /// bypasses <see cref="DualDeckTransport.Target"/>. The two loads above each
    /// set the target from their own arguments, so a take that loaded without a
    /// discovered partner left it on Active: deck B stayed cued and silent and no
    /// sync lock was ever set. Nothing else re-armed it later, and the UI tick
    /// only reads the target back, so the pair never recovered. This is the one
    /// place a karaoke play begins, so the pair arms Both and the lock here.</para>
    /// </summary>
    private void LoadKaraokePair(TrackEntry track, AudioVariant variant = AudioVariant.Karaoke)
    {
        ReleaseKaraokePair(); // idempotent: a second pair reloads both decks

        var karaokePath = VariantOnDisk(track, variant);
        if (karaokePath == null || string.IsNullOrWhiteSpace(track.MusicPath)) return;

        // Which resident reader the take drives. BuildVersions maps Karaoke and
        // Backing to the karaoke reader (one render, two names) and Vocals to the
        // third reader, which SetTrack fills from the primary path itself, so the
        // take is the sub-stem only when it is a karaoke render.
        var takeStem = variant == AudioVariant.Vocals ? StemKind.Vocals : StemKind.Karaoke;

        // The original file is always the deck's primary stem; the take rides
        // beside it as the sub-stem. Loading the take as the primary put deck A
        // on the wrong side of that contract: it labelled the render "Original",
        // and Karaoke mode had no second reader to unmute.
        DeckA.SetTrack(track.MusicPath, takeStem == StemKind.Karaoke ? karaokePath : null,
                       track.Title, track.Artist);
        DeckB.SetTrack(track.MusicPath, null, track.Title, track.Artist);
        // One take per deck: a load discovers the sibling render beside its file
        // as a partner, and the deck's default Mix mode would then sum both files,
        // so each deck is pinned to its own take.
        DeckB.HoldStem(StemKind.Original);
        DeckA.HoldStem(takeStem, karaokePath, variant);

        IsKaraokePairLoaded = true;

        // The pair bank rides the pair's lifetime: built after both decks have
        // their track (so the cue rows name a real pair) and dropped in
        // ReleaseKaraokePair. Both channels are seeked directly, not mirrored.
        PairCues = new PairCueBankViewModel(
            () => MasterDeck.MediaSeconds,
            seconds =>
            {
                DeckA.SeekMedia(seconds);
                DeckB.SeekMedia(seconds);
            },
            () => PairHotCueStore);
        PairCues.SetPair(DeckA.Cues.TrackId, DeckB.Cues.TrackId);
        // The take's own name from the variant, never a literal: "Deck A: Karaoke"
        // is this deck's identity plus whatever take the operator picked.
        DeckA.SetKaraokeTitle(KaraokeNaming.DisplayName(variant));
        DeckB.SetKaraokeTitle(KaraokeNaming.DisplayName(AudioVariant.Original));

        _activeDeck = PlaybackDeck.A;
        _transport.ActivateDeck(PlaybackDeck.A);
        SyncDeckActivation();

        // Explicit, not inferred: the last load above is the last word on the
        // target, and PlayDeck would ignore it anyway.
        _transport.SetTarget(TransportTarget.Both);
        IsABMode = true;
        IsDeckSync = true;
        _pairStartLogged = false;
        Log.Info($"karaoke pair: start take={variant} file={Path.GetFileName(karaokePath)} " +
                 $"target={_transport.Target} sync={_transport.SyncActive}");

        // Play() fans out over Targets(), so both loaded decks roll. A deck whose
        // load has not started yet refuses a play outright, so each one is also
        // armed on its own loaded transition; whichever lane wins, the start is
        // not lost between the load and the first frame.
        DeckA.StartWhenLoaded();
        DeckB.StartWhenLoaded();
        _transport.Play();
    }

    /// <summary>
    /// Give the decks their own names back. One choke point on purpose:
    /// <c>LoadTrack</c> calls it first on every ordinary load, which is why the
    /// pair path never calls <c>LoadTrack</c> itself; the only other caller is the
    /// stop guard in <c>OnUiTick</c>.
    /// </summary>
    private void ReleaseKaraokePair()
    {
        if (!IsKaraokePairLoaded) return;
        IsKaraokePairLoaded = false;
        PairCues = null;
        DeckA.RestoreTitle();
        DeckB.RestoreTitle();
    }

    /// <summary>True when a karaoke action can run: the song and its partner are both on disk.</summary>
    internal static bool HasKaraokePairOnDisk(TrackEntry? t)
        => t != null
        && !string.IsNullOrWhiteSpace(t.MusicPath)
        && (StemOnDisk(t) ?? StemBeside(t.MusicPath)) != null;

    /// <summary>
    /// Selftest only: start a pair for a library row, exactly as the menu does.
    /// The capture harness cannot select a tree node by title, and a pair is the
    /// only state that renders both deck labels, so this is the one seam.
    /// </summary>
    internal void StartKaraokePairForCapture(TrackEntry track) => LoadKaraokePair(track);

    /// <summary>
    /// Selftest only: the same original on both decks, so a capture can photograph
    /// the worst case for the deck cards - two cards that each hold a karaoke stem
    /// and therefore both draw the strip's stem trim. The pair cannot stand in for
    /// it: Deck A takes the karaoke render, which is the sub-stem itself and so
    /// reports no stem of its own.
    /// </summary>
    internal void StartBothDecksForCapture(TrackEntry track)
    {
        LoadTrack(track, targetDeck: PlaybackDeck.A, activate: false);
        LoadTrack(track, targetDeck: PlaybackDeck.B, activate: false);
    }

    // ---- menu actions -------------------------------------------------------
    // Both resolve their row exactly like "Play": the tree selection, no command
    // parameter. The commands stay present (the rows are always drawn where a pair
    // can exist) and go disabled on a row without a karaoke file on disk.

    [RelayCommand(CanExecute = nameof(CanKaraokeSelected))]
    private void PlayKaraokeSelected(AudioVariant? variant)
    {
        if (SelectedTreeNode?.Track is not { } track) return;
        LoadKaraokePair(track, variant ?? AudioVariant.Karaoke);
    }

    /// <summary>
    /// Play one named take of a row: the per-variant menu item calls this. The
    /// original is an ordinary single-deck load; every other take is the pair.
    /// </summary>
    private void PlayVariant(TrackEntry? track, AudioVariant variant)
    {
        if (track == null) return;

        if (variant == AudioVariant.Original) LoadTrack(track, fromQueue: false);
        else LoadKaraokePair(track, variant);
    }

    [RelayCommand(CanExecute = nameof(CanKaraokeSelected))]
    private void QueueKaraokeSelected()
    {
        if (SelectedTreeNode?.Track is { } track) EnqueueKaraokePair(track);
    }

    /// <summary>
    /// Queue a karaoke pair as one unit: the karaoke row on A and the original on
    /// B, sharing one <c>PairKey</c>, both or neither.
    /// </summary>
    private void EnqueueKaraokePair(TrackEntry track)
    {
        var (karaoke, original) = PlaybackQueueItem.FromKaraokePair(track);
        Enqueue(karaoke);
        Enqueue(original);
        StatusText = $"Queued \u201c{track.Title}\u201d as a karaoke pair "
                   + "(karaoke on deck A, original on deck B).";
    }

    private bool CanKaraokeSelected() => HasKaraokePairOnDisk(SelectedTreeNode?.Track);

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// The file for one take: the partner the scan paired first (it may live in
    /// the karaoke root, outside the song's folder), then the sibling on disk.
    /// </summary>
    private static string? VariantOnDisk(TrackEntry? track, AudioVariant variant)
    {
        if (track == null) return null;

        if (variant == AudioVariant.Karaoke)
            return StemOnDisk(track) ?? KaraokeNaming.VariantPathFor(track.MusicPath, variant);

        return KaraokeNaming.VariantPathFor(track.MusicPath, variant);
    }

    /// <summary>The karaoke partner of a row, but only when the file is really there.</summary>
    private static string? StemOnDisk(TrackEntry? track)
    {
        var path = track?.KaraokePath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        // The service stamps KaraokePath with whatever it last rendered, the
        // vocals take included. A vocals file is never the karaoke stem; only
        // reject that name, because a karaoke-root copy has no marker at all.
        if (Path.GetFileNameWithoutExtension(path)
                .EndsWith(KaraokeNaming.VocalsMarker, StringComparison.OrdinalIgnoreCase))
            return null;

        try { return File.Exists(path) ? path : null; }
        catch { return null; }
    }

    /// <summary>Probe the naming rule for a stem beside a file the library has no row for.</summary>
    private static string? StemBeside(string? musicPath)
    {
        if (string.IsNullOrWhiteSpace(musicPath)) return null;

        var exact = KaraokeNaming.KaraokePathFor(musicPath);
        try
        {
            if (!string.IsNullOrEmpty(exact) && File.Exists(exact)) return exact;

            var folder = Path.GetDirectoryName(musicPath);
            if (string.IsNullOrEmpty(folder)) return null;

            // A generated stem may use another format beside a downloaded original.
            var baseName = KaraokeNaming.BaseFileName(musicPath);
            foreach (var ext in new[] { ".flac", ".wav", ".mp3", ".m4a", ".ogg", ".opus" })
            {
                var candidate = Path.Combine(folder, baseName + KaraokeNaming.Marker + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* unreadable path: no stem */ }

        return null;
    }
}

using System.IO;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Library;
using JockeyBox.Playback;

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
    private bool _karaokePairLoaded;

    /// <summary>
    /// Load the pair: the karaoke file on A, playing at once (hard cut), and the
    /// original on B, loaded and cued at 0:00 with its own transport fully live.
    /// The second <c>SetTrack</c> argument is always null - a deck in a pair plays
    /// one file, exactly like any other load.
    /// </summary>
    private void LoadKaraokePair(TrackEntry track)
    {
        ReleaseKaraokePair(); // idempotent: a second pair reloads both decks

        var karaokePath = StemOnDisk(track) ?? StemBeside(track.MusicPath);
        if (karaokePath == null || string.IsNullOrWhiteSpace(track.MusicPath)) return;

        DeckA.SetTrack(karaokePath, null, track.Title, track.Artist);
        DeckB.SetTrack(track.MusicPath, null, track.Title, track.Artist);
        _karaokePairLoaded = true;
        DeckA.SetKaraokeTitle("DECK: KARAOKE");
        DeckB.SetKaraokeTitle("DECK: ORIGINAL");

        _activeDeck = PlaybackDeck.A;
        _transport.PlayDeck(PlaybackDeck.A, autoCue: false);
        _transport.ActivateDeck(PlaybackDeck.A);
        SyncDeckActivation();
    }

    /// <summary>
    /// Give the decks their own names back. One choke point on purpose:
    /// <c>LoadTrack</c> calls it first on every ordinary load, which is why the
    /// pair path never calls <c>LoadTrack</c> itself; the only other caller is the
    /// stop guard in <c>OnUiTick</c>.
    /// </summary>
    private void ReleaseKaraokePair()
    {
        if (!_karaokePairLoaded) return;
        _karaokePairLoaded = false;
        DeckA.RestoreTitle();
        DeckB.RestoreTitle();
    }

    /// <summary>True when a karaoke action can run: the song and its partner are both on disk.</summary>
    internal static bool HasKaraokePairOnDisk(TrackEntry? t)
        => t != null
        && !string.IsNullOrWhiteSpace(t.MusicPath)
        && (StemOnDisk(t) ?? StemBeside(t.MusicPath)) != null;

    // ---- menu actions -------------------------------------------------------
    // Both resolve their row exactly like "Play": the tree selection, no command
    // parameter. The commands stay present (the rows are always drawn where a pair
    // can exist) and go disabled on a row without a karaoke file on disk.

    [RelayCommand(CanExecute = nameof(CanKaraokeSelected))]
    private void PlayKaraokeSelected()
    {
        if (SelectedTreeNode?.Track is { } track) LoadKaraokePair(track);
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

    /// <summary>The karaoke partner of a row, but only when the file is really there.</summary>
    private static string? StemOnDisk(TrackEntry? track)
    {
        var path = track?.KaraokePath;
        if (string.IsNullOrWhiteSpace(path)) return null;
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

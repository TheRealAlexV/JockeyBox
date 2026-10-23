using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Playback;
using JockeyBox.Sources;

namespace JockeyBox.ViewModels;

/// <summary>
/// One row of the playback queue. A row is either a library
/// <see cref="TrackEntry"/> or a remote <see cref="SourceTrack"/> (Spotify /
/// YouTube), so the QUEUED tab can show both without caring which is which.
///
/// <see cref="IsCurrent"/> is the row playing right now, <see cref="IsPlayed"/>
/// the rows finished in this run of the queue - played rows stay in the list
/// (greyed in the UI) instead of being removed.
///
/// <see cref="DeckTarget"/> is the deck this row plays on. Enqueue assigns it
/// from the A/B rotation (see <c>MainViewModel.AssignDeckForNewRow</c>) and the
/// user can flip it per row; auto-advance loads the successor on its own deck.
/// </summary>
public partial class PlaybackQueueItem : ObservableObject
{
    /// <summary>Persisted kind for library rows.</summary>
    public const string LibraryKind = "library";

    /// <summary>Persisted kind for Spotify / YouTube rows.</summary>
    public const string RemoteKind = "remote";

    private PlaybackQueueItem(string key, string title, string artist,
                              string durationText, TrackEntry? track, SourceTrack? source,
                              PlaybackDeck deckTarget)
    {
        Key = key;
        Title = title;
        Artist = artist;
        DurationText = durationText;
        Track = track;
        Source = source;
        DeckTarget = deckTarget;
    }

    /// <summary>Stable identity: <c>RelativeKey</c> locally, remote id otherwise.</summary>
    public string Key { get; private set; }

    public bool IsLibrary => Track != null;
    public bool IsRemote => Source != null;
    public string Kind => IsLibrary ? LibraryKind : RemoteKind;

    /// <summary>Library entry when this is a library row.</summary>
    public TrackEntry? Track { get; private set; }

    /// <summary>
    /// Follow a library rename/move: the row keeps its position and played
    /// flag, but resolves to the new key and, after a rescan, the new entry.
    /// </summary>
    internal void Repoint(string key, TrackEntry? track)
    {
        if (!string.IsNullOrWhiteSpace(key)) Key = key;
        if (track != null)
        {
            Track = track;
            // A rename changes what the row says, even though its identity is
            // the same queue slot.
            if (!string.Equals(Title, track.Title, StringComparison.Ordinal))
            {
                Title = track.Title;
                OnPropertyChanged(nameof(Title));
            }
            if (!string.Equals(Artist, track.Artist, StringComparison.Ordinal))
            {
                Artist = track.Artist;
                OnPropertyChanged(nameof(Artist));
            }
        }
        OnPropertyChanged(nameof(Track));
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(Kind));
    }

    /// <summary>Remote track when this is a Spotify / YouTube row.</summary>
    public SourceTrack? Source { get; }

    public string Title { get; private set; }
    public string Artist { get; private set; }
    public string DurationText { get; }

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isPlayed;

    /// <summary>Row number shown in the tab; assigned by the queue owner.</summary>
    [ObservableProperty] private int _index;

    /// <summary>1-based position text for the UI ("1", "2", …).</summary>
    public string IndexText => Index.ToString();

    /// <summary>Short state marker: current, played, or nothing.</summary>
    public string Display => IsCurrent ? "\u25B6 playing" : IsPlayed ? "played" : "";

    /// <summary>
    /// Deck this row plays on. Set when the row is enqueued (A/B rotation) or by
    /// the user's "send to deck" action; persisted with the queue.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeckText))]
    [NotifyPropertyChangedFor(nameof(DeckHint))]
    private PlaybackDeck _deckTarget;

    /// <summary>Badge in the QUEUED row ("A"/"B").</summary>
    public string DeckText => DeckTarget.ToPersistedString();

    /// <summary>Hover hint for the badge: what it means and what clicking does.</summary>
    public string DeckHint => IsCurrent
        ? $"Playing on deck {DeckText} (the running row keeps its deck)"
        : $"Queued for deck {DeckText} - click to send it to deck {DeckTarget.Opposite().ToPersistedString()}";

    partial void OnIndexChanged(int value) => OnPropertyChanged(nameof(IndexText));

    partial void OnIsCurrentChanged(bool value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(DeckHint));
    }

    partial void OnIsPlayedChanged(bool value) => OnPropertyChanged(nameof(Display));

    public static PlaybackQueueItem FromTrack(TrackEntry track, PlaybackDeck deckTarget) => new(
        track.RelativeKey,
        string.IsNullOrWhiteSpace(track.Title) ? track.RelativePath : track.Title,
        track.Artist,
        Format(track.Duration),
        track,
        null,
        deckTarget);

    public static PlaybackQueueItem FromSource(SourceTrack source, PlaybackDeck deckTarget) => new(
        RemoteKeyFor(source),
        string.IsNullOrWhiteSpace(source.Title) ? source.DisplayName : source.Title,
        source.Artist,
        Format(source.Duration),
        null,
        source,
        deckTarget);

    /// <summary>Rebuild a library row that was persisted before the library scan.</summary>
    public static PlaybackQueueItem FromRecord(PlaybackQueueRecord record, TrackEntry? track) => new(
        record.Key,
        string.IsNullOrWhiteSpace(record.Title) ? record.Key : record.Title,
        record.Artist,
        track != null ? Format(track.Duration) : "",
        track,
        null,
        PlaybackDecks.TryParse(record.Deck, out var deck) ? deck : PlaybackDeck.A)
    {
        IsPlayed = record.Played,
    };

    /// <summary>
    /// Remote identity: the source id when there is one (stable across runs),
    /// otherwise source + cleaned media title + title, so an id-less track is at
    /// least not confused with its neighbours.
    /// </summary>
    public static string RemoteKeyFor(SourceTrack source)
    {
        if (!string.IsNullOrWhiteSpace(source.Id)) return source.Id;

        var (cleanTitle, _) = MediaTitle.Clean(source.Title, source.Artist);
        return $"{source.Source}\u001f{cleanTitle}\u001f{source.Title}";
    }

    private static string Format(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "";
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
    }
}

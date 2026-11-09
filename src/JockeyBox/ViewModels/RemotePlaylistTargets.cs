using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Sources;

namespace JockeyBox.ViewModels;

/// <summary>
/// Everything the remote tabs (Spotify / YouTube) need from the rest of the app
/// that they must not reach for themselves: the karaoke index, and the one path
/// that adds a remote row to an <em>internal</em> playlist.
///
/// <para>Built once in <see cref="MainViewModel"/> and handed to both tabs, so
/// neither one learns how playlists are stored.</para>
/// </summary>
public sealed class RemoteTrackHost
{
    public required RemoteLibraryFacts Karaoke { get; init; }

    /// <summary>Add a remote row to an internal playlist; returns rows added.</summary>
    public required Func<SourceTrack, UserPlaylist, int> AddToPlaylist { get; init; }

    /// <summary>Queue karaoke separation for the row's local file (no-op when there is none).</summary>
    public required Action<SourceTrack> MakeKaraoke { get; init; }

    /// <summary>Show the row's local file in Explorer (no-op when there is none).</summary>
    public required Action<SourceTrack> RevealLocalFile { get; init; }
}

/// <summary>
/// The "Add to playlist ▸" menu shared by both remote tabs.
///
/// <para>It lists the user's own playlists - the app-owned ("internal") ones -
/// plus a create-and-add entry. A remote track that has not been downloaded yet
/// can be added: <see cref="PlaylistStore.AddRemoteTracks"/> keeps its source
/// identity, so the playlist shows it immediately and swaps in the library file
/// once one exists.</para>
///
/// <para>WPF refuses to bind a menu item collection that changes while its
/// popup is open, so the list is a snapshot (<see cref="Refresh"/>) taken by the
/// owner of the tab when the store changes or a menu action runs.</para>
/// </summary>
public sealed partial class RemotePlaylistTargets : ObservableObject
{
    private readonly PlaylistStore _store;
    private readonly Func<SourceTrack, UserPlaylist, int> _add;

    public RemotePlaylistTargets(PlaylistStore store, Func<SourceTrack, UserPlaylist, int> add)
    {
        _store = store;
        _add = add;
        Refresh();
    }

    /// <summary>The user's own playlists, in rail order.</summary>
    public ObservableCollection<UserPlaylist> Playlists { get; } = new();

    /// <summary>
    /// The submenu's rows: one per playlist, then the trailing "New playlist…".
    /// A single flat list because a WPF <c>MenuItem</c> that is filled from
    /// <c>ItemsSource</c> cannot also carry a hand-written child.
    /// </summary>
    public ObservableCollection<RemotePlaylistChoice> Choices { get; } = new();

    /// <summary>False when the user has no internal playlist yet: the menu shows only "New playlist…".</summary>
    public bool HasPlaylists => Playlists.Count > 0;
    public bool HasNoPlaylists => !HasPlaylists;

    /// <summary>Re-read the store. Cheap: one pass over the playlist headers.</summary>
    public void Refresh()
    {
        Playlists.Clear();
        foreach (var playlist in _store.Playlists)
        {
            if (playlist.IsInternal) Playlists.Add(playlist);
        }

        Choices.Clear();
        foreach (var playlist in Playlists) Choices.Add(new RemotePlaylistChoice(playlist.Name, playlist));
        Choices.Add(RemotePlaylistChoice.New);

        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(HasNoPlaylists));
    }

    /// <summary>Add one remote row to one playlist; returns rows added (0 = already there).</summary>
    public int Add(SourceTrack? track, UserPlaylist? playlist)
    {
        if (track is null || playlist is null) return 0;
        return _add(track, playlist);
    }

    /// <summary>
    /// Create an internal playlist and, when a row is given, put it inside.
    /// "New playlist…" in the submenu must do both in one gesture, the way
    /// Spotify's own "Create playlist" entry does.
    /// </summary>
    public UserPlaylist CreateAndAdd(string? name, SourceTrack? track)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) trimmed = "New playlist";

        // Playlist names are unique (the rail and the database both assume it), so
        // pressing "New playlist…" twice must not make two rows with one name.
        var unique = trimmed;
        for (var n = 2; _store.FindByName(unique) is not null; n++)
        {
            unique = $"{trimmed} {n}";
        }

        var playlist = _store.CreateInternal(unique);
        Refresh();
        if (track is not null) _add(track, playlist);
        return playlist;
    }

    /// <summary>
    /// Run one submenu row: add to that playlist, or create a fresh one and put
    /// the row inside. The created list takes the default name (the menu entry is
    /// a single click, not a form) and is renamed from the rail afterwards.
    /// </summary>
    public void Choose(RemotePlaylistChoice? choice, SourceTrack? track)
    {
        if (choice is null) return;
        if (choice.Playlist is null) CreateAndAdd(null, track);
        else Add(track, choice.Playlist);
    }
}

/// <summary>One row of the "Add to playlist ▸" submenu.</summary>
public sealed record RemotePlaylistChoice(string Header, UserPlaylist? Playlist)
{
    /// <summary>The trailing create-and-add row (<see cref="Playlist"/> is null).</summary>
    public static RemotePlaylistChoice New { get; } = new("New playlist\u2026", null);
}

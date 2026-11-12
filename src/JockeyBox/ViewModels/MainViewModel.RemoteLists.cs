using System.Diagnostics;
using System.IO;
using JockeyBox.Library;
using JockeyBox.Separation;
using JockeyBox.Sources;

namespace JockeyBox.ViewModels;

/// <summary>
/// The bridge between the remote tabs (Spotify / YouTube) and the rest of the app.
///
/// <para>Both tabs list tracks that may or may not be on disk, and both must offer
/// the local library's verbs where they make sense: add to one of the user's own
/// playlists, show whether a karaoke version exists, separate one, reveal the
/// local file, copy the link. None of that belongs in a tab's view model - the
/// tab must not learn how playlists are stored or where the separation service
/// lives - so this partial owns it and hands each tab a single
/// <see cref="RemoteTrackHost"/>.</para>
///
/// <para>Karaoke stays cheap on purpose. The tabs can hold thousands of rows and
/// re-derive them after every queue event, so the answer comes from
/// <see cref="_remoteFacts"/>: one pass over the scan's results, then one
/// dictionary hit per row, never a ledger walk or a disk probe. That index is
/// rebuilt exactly where <see cref="LibraryIndex"/> is, so the two can never
/// disagree about which file a row resolves to.</para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>Scan-derived "is it here, is there karaoke?" index for the remote tabs.</summary>
    private readonly RemoteLibraryFacts _remoteFacts = new();

    /// <summary>What the tabs get; built once in <see cref="InitializeRemoteLists"/>.</summary>
    private RemoteTrackHost? _remoteHost;

    /// <summary>
    /// Build the host and subscribe the tabs' menus to playlist writes. Called
    /// from the constructor before the tabs are created, so neither tab ever
    /// starts without its menu data.
    /// </summary>
    private void InitializeRemoteLists()
    {
        _remoteHost = new RemoteTrackHost
        {
            Karaoke = _remoteFacts,
            AddToPlaylist = AddRemoteToPlaylist,
            MakeKaraoke = MakeKaraokeForRemote,
            RevealLocalFile = RevealRemoteFile,
        };

        // A playlist written anywhere (this window, the other tab, an import)
        // must show up in both "Add to playlist" submenus without a restart.
        _playlistStore.Changed += () => System.Windows.Application.Current?.Dispatcher
            .BeginInvoke(() =>
            {
                Spotify.RefreshLibraryPlaylists();
                YouTube.RefreshLibraryPlaylists();
            });
    }

    /// <summary>
    /// Re-derive every remote row from the current scan. Runs on the UI thread
    /// after a rescan and after a delete, which are the only two things that can
    /// change the answer.
    /// </summary>
    private void RefreshRemoteLibraryFacts()
    {
        // Rescan can fire before the tabs exist (first scan of the session).
        if (Spotify is not null) Spotify.ApplyLibraryFacts();
        if (YouTube is not null) YouTube.ApplyLibraryFacts();
    }

    /// <summary>
    /// Add one remote row to one of the user's own playlists. The row is stored
    /// by its remote identity, so a track that has not been downloaded yet still
    /// appears in the playlist and plays through the resolver; once a local file
    /// exists the playlist's row upgrades to it (LibrarySnapshot.Resolve).
    /// </summary>
    private int AddRemoteToPlaylist(SourceTrack track, UserPlaylist playlist)
    {
        if (playlist is not { IsInternal: true }) return 0;

        var added = _playlistStore.AddRemoteTracks(playlist, new[] { track });
        if (added == 0)
        {
            StatusText = $"\u201c{track.DisplayName}\u201d is already in {playlist.Name}.";
            return 0;
        }

        RefreshPlaylistRail();
        ApplyPlaylistFilter(_activePlaylist);
        StatusText = $"Added \u201c{track.DisplayName}\u201d to {playlist.Name}.";
        return added;
    }

    /// <summary>
    /// "Separate karaoke" on a remote row: only meaningful once the file is here,
    /// and the menu entry is disabled until then. Runs the same command the
    /// library browser runs, so progress and the resulting file are identical.
    /// </summary>
    private void MakeKaraokeForRemote(SourceTrack track)
    {
        if (_remoteFacts.Find(track) is not { } entry)
        {
            StatusText = $"\u201c{track.DisplayName}\u201d is not in the library yet.";
            return;
        }

        EnqueueKaraoke(entry, KaraokeStemMode.Instrumental);
    }

    /// <summary>Show a remote row's local file in Explorer (no-op when it has none).</summary>
    private void RevealRemoteFile(SourceTrack track)
    {
        var path = _remoteFacts.Find(track) is { } entry
            ? AudioPathOf(entry) ?? entry.KaraokePath
            : null;

        path ??= _libraryIndex.Resolve(track);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText = $"\u201c{track.DisplayName}\u201d is not in the library yet.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
            StatusText = $"Showing \u201c{Path.GetFileName(path)}\u201d in Explorer.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open Explorer: {ex.Message}";
        }
    }

}

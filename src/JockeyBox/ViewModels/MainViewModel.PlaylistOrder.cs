using CommunityToolkit.Mvvm.Input;
using JockeyBox.Sources;

namespace JockeyBox.ViewModels;

/// <summary>
/// Rail ordering: Move up / Move down for the LIBRARY playlist context menu.
/// Order lives in <see cref="UserPlaylist.SortOrder"/> and is renumbered by
/// <see cref="PlaylistStore.MovePlaylist"/>, so this file only decides whether
/// a move is possible and reports what changed.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanMovePlaylistUp))]
    private void MovePlaylistUp(PlaylistRailItem? row) => MovePlaylist(row, -1);

    [RelayCommand(CanExecute = nameof(CanMovePlaylistDown))]
    private void MovePlaylistDown(PlaylistRailItem? row) => MovePlaylist(row, +1);

    private bool CanMovePlaylistUp(PlaylistRailItem? row) => CanMovePlaylist(row, -1);

    private bool CanMovePlaylistDown(PlaylistRailItem? row) => CanMovePlaylist(row, +1);

    /// <summary>
    /// The first row cannot move up and the last cannot move down: the command
    /// is disabled rather than clamping the move into a duplicate position.
    /// Imported rows are read-only in this rail, matching Rename/Delete.
    /// </summary>
    private bool CanMovePlaylist(PlaylistRailItem? row, int delta) =>
        _playlistStore.CanMovePlaylist(row?.Playlist, delta);

    private void MovePlaylist(PlaylistRailItem? row, int delta)
    {
        if (row?.Playlist is not { } playlist) return;
        if (!_playlistStore.MovePlaylist(playlist, delta)) return;

        RefreshPlaylistRail();
        MovePlaylistUpCommand.NotifyCanExecuteChanged();
        MovePlaylistDownCommand.NotifyCanExecuteChanged();
        StatusText = delta < 0
            ? $"moved playlist {playlist.Name} up"
            : $"moved playlist {playlist.Name} down";
    }
}

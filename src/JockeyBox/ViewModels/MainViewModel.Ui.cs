using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Library;

namespace JockeyBox.ViewModels;

// ===========================================================================
// UI contract for the shared XAML in Views/Shared.
//
// Everything here exists because MainWindow.xaml, SpotifyView.xaml and
// YouTubeView.xaml now share one row design, one context menu and one
// filter/sort strip. The behaviour lane only has to fill these in; no XAML
// change is needed to hook them up.
// ===========================================================================

/// <summary>Column a shared track list sorts by.</summary>
public enum TrackSortField
{
    TrackName,
    Artist,
    Bpm,
    Key,
    Length,
}

/// <summary>One entry in the shared sort selector.</summary>
public sealed record TrackSortChoice(TrackSortField Value, string Label);

/// <summary>Sort fields offered by every list.</summary>
public static class TrackSortChoices
{
    public static IReadOnlyList<TrackSortChoice> All { get; } = new[]
    {
        new TrackSortChoice(TrackSortField.TrackName, "Track name"),
        new TrackSortChoice(TrackSortField.Artist, "Artist"),
        new TrackSortChoice(TrackSortField.Bpm, "BPM"),
        new TrackSortChoice(TrackSortField.Key, "Key"),
        new TrackSortChoice(TrackSortField.Length, "Length"),
    };
}

/// <summary>
/// Filter + sort state for one list, and the DataContext of the shared strip.
///
/// <para>The strip is a control, not the behaviour: when a list already has a
/// working filter, the host passes <c>onFilter</c> and the strip writes straight
/// into it. When it passes nothing, the state still lands here, ready for the
/// behaviour lane to read <see cref="FilterText"/>, <see cref="SortField"/> and
/// <see cref="SortDescending"/> without touching the XAML again.</para>
/// </summary>
public partial class TrackListView : ObservableObject
{
    private readonly Action<string>? _onFilter;
    private readonly Action<TrackSortField, bool>? _onSort;

    public TrackListView(string filterHint, Action<string>? onFilter = null,
        Action<TrackSortField, bool>? onSort = null)
    {
        FilterHint = filterHint;
        _onFilter = onFilter;
        _onSort = onSort;
    }

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private TrackSortField _sortField = TrackSortField.TrackName;
    [ObservableProperty] private bool _sortDescending;

    /// <summary>Read by the box's tooltip and its automation name.</summary>
    public string FilterHint { get; }

    public string DirectionLabel => SortDescending ? "DESC" : "ASC";

    public string DirectionToolTip => SortDescending
        ? "Sorted descending. Click for ascending."
        : "Sorted ascending. Click for descending.";

    partial void OnFilterTextChanged(string value) => _onFilter?.Invoke(value);

    partial void OnSortFieldChanged(TrackSortField value) => _onSort?.Invoke(value, SortDescending);

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(DirectionLabel));
        OnPropertyChanged(nameof(DirectionToolTip));
        _onSort?.Invoke(SortField, value);
    }

    [RelayCommand]
    private void ToggleDirection() => SortDescending = !SortDescending;
}

/// <summary>One row of the shared menu's "Add to Playlist" submenu.</summary>
public sealed record PlaylistTargetItem(string Name, ICommand Command);

/// <summary>
/// What one surface can do with the row under the cursor. The shared
/// <c>TrackMenu</c> binds every item to one of these and hides the item when the
/// command is absent, so a surface can never show an action it cannot perform.
/// </summary>
public sealed class TrackMenuCapabilities : ObservableObject
{
    public ICommand? Play { get; init; }
    public ICommand? Preview { get; init; }
    public ICommand? AddToQueue { get; init; }

    /// <summary>"Add to Playlist" as a submenu of the user's playlists.</summary>
    public ObservableCollection<PlaylistTargetItem> PlaylistTargets { get; } = new();

    /// <summary>"Add to Playlist" as one gesture, used only when the submenu is empty.</summary>
    public ICommand? AddToPlaylist { get; init; }

    public ICommand? Download { get; init; }
    public ICommand? MakeKaraoke { get; init; }
    public ICommand? ShowInExplorer { get; init; }
    public ICommand? Tags { get; init; }

    /// <summary>
    /// "Find on YouTube" on a track row. Takes no parameter on purpose: it reads
    /// the row the surface selected and opens the YouTube search window filled
    /// with that row's "Artist Title", so the command carries a query rather than
    /// a video id - a row with no upload picked yet has no id to carry.
    /// </summary>
    public ICommand? FindOnYouTube { get; init; }
    public ICommand? FindOnSpotify { get; init; }

    public bool CanPlay => Play is not null;
    public bool CanPreview => Preview is not null;
    public bool CanAddToQueue => AddToQueue is not null;
    public bool CanChoosePlaylist => PlaylistTargets.Count > 0;
    public bool CanAddToPlaylistDirectly => PlaylistTargets.Count == 0 && AddToPlaylist is not null;
    public bool CanDownload => Download is not null;
    public bool CanMakeKaraoke => MakeKaraoke is not null;
    public bool CanShowInExplorer => ShowInExplorer is not null;
    public bool CanEditTags => Tags is not null;
    public bool CanFindOnYouTube => FindOnYouTube is not null;
    public bool CanFindOnSpotify => FindOnSpotify is not null;

    /// <summary>
    /// Raised after a surface refills <see cref="PlaylistTargets"/>, so the menu's
    /// submenu/single-item choice is re-evaluated.
    /// </summary>
    public void NotifyStructureChanged()
    {
        OnPropertyChanged(nameof(CanChoosePlaylist));
        OnPropertyChanged(nameof(CanAddToPlaylistDirectly));
    }
}

public partial class MainViewModel
{
    private TrackListView? _libraryView;
    private TrackMenuCapabilities? _trackMenu;

    private TrackSortField _librarySortField = TrackSortField.TrackName;
    private bool _librarySortDescending;

    /// <summary>
    /// Filter + sort for the library tree. The filter drives the tree's existing
    /// search; the sort fields drive the leaf order in <see cref="BuildTree"/>,
    /// including the playlist path.
    /// </summary>
    public TrackListView LibraryView => _libraryView ??= new TrackListView(
        "Filter the library by title, artist or folder",
        text => SearchText = text,
        SetLibrarySort);

    /// <summary>
    /// Store the sort strip's choice and rebuild through the one tree-refresh
    /// path, so a sort change never starts a second builder beside the search one.
    /// </summary>
    private void SetLibrarySort(TrackSortField field, bool descending)
    {
        if (_librarySortField == field && _librarySortDescending == descending) return;
        _librarySortField = field;
        _librarySortDescending = descending;
        RebuildBrowser();
    }

    /// <summary>
    /// Library capabilities for the shared context menu, read by
    /// MainWindow.xaml through <c>TrackTree.Tag</c>.
    /// </summary>
    public TrackMenuCapabilities TrackMenu => _trackMenu ??= BuildTrackMenu();

    private TrackMenuCapabilities BuildTrackMenu()
    {
        var caps = new TrackMenuCapabilities
        {
            Play = LoadSelectedTreeTrackCommand,
            AddToQueue = AddToQueueCommand,
            MakeKaraoke = new RelayCommand<object?>(item =>
            {
                if (UnwrapTrack(item) is { } track && MakeKaraokeCommand.CanExecute(track))
                    MakeKaraokeCommand.Execute(track);
            }),
            ShowInExplorer = RevealInExplorerCommand,
        };

        RebuildPlaylistTargets(caps);
        InternalPlaylists.CollectionChanged += (_, _) => RebuildPlaylistTargets(caps);
        return caps;
    }

    private void RebuildPlaylistTargets(TrackMenuCapabilities caps)
    {
        caps.PlaylistTargets.Clear();
        foreach (var playlist in InternalPlaylists)
        {
            var target = playlist;
            caps.PlaylistTargets.Add(new PlaylistTargetItem(
                target.Name,
                new RelayCommand(() =>
                {
                    if (AddSelectedToPlaylistCommand.CanExecute(target))
                        AddSelectedToPlaylistCommand.Execute(target);
                })));
        }
    }

    /// <summary>
    /// Park the active deck at the start of its track. The main transport's
    /// previous button runs this on a single press and <see cref="PlayPreviousCommand"/>
    /// on a double press.
    /// </summary>
    [RelayCommand]
    private void RestartTrack()
    {
        var deck = DeckA.IsActive ? DeckA : DeckB;
        if (deck.RestartCommand.CanExecute(null)) deck.RestartCommand.Execute(null);
    }

    /// <summary>Previous track for one deck: the deck card's own previous button.</summary>
    [RelayCommand]
    private void PreviousTrackForDeck(DeckViewModel? deck) => StepQueueForDeck(deck, back: true);

    /// <summary>Next track for one deck: the deck card's own next button.</summary>
    [RelayCommand]
    private void NextTrackForDeck(DeckViewModel? deck) => StepQueueForDeck(deck, back: false);

    /// <summary>
    /// Activating the deck first is the point: a per-deck transport button must
    /// never move a deck the user is not looking at.
    /// </summary>
    private void StepQueueForDeck(DeckViewModel? deck, bool back)
    {
        if (deck is null) return;
        if (deck.ActivateCommand.CanExecute(null)) deck.ActivateCommand.Execute(null);
        var command = back ? PlayPreviousCommand : (ICommand)PlayNextCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    private static TrackEntry? UnwrapTrack(object? item) => item switch
    {
        BrowserNode node => node.Track,
        TrackEntry entry => entry,
        _ => null,
    };
}

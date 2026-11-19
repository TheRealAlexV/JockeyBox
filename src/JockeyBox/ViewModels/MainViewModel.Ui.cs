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
    DateAdded,
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

    /// <summary>The Spotify list only: it is the surface whose rows carry a date added.</summary>
    public static IReadOnlyList<TrackSortChoice> Spotify { get; } = new List<TrackSortChoice>(All)
    {
        new TrackSortChoice(TrackSortField.DateAdded, "Date added"),
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

    /// <summary>Sort fields this list offers; the library and YouTube keep the default.</summary>
    public IReadOnlyList<TrackSortChoice> SortChoices { get; init; } = TrackSortChoices.All;

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

    /// <summary>
    /// The karaoke render, as three modes. A library row can express all three,
    /// so it gets the submenu; a surface that can only ask for the plain
    /// instrumental sets <see cref="MakeKaraoke"/> alone and gets one item.
    /// </summary>
    public ICommand? MakeKaraoke { get; init; }
    public ICommand? MakeKaraokeKeepBacking { get; init; }
    public ICommand? MakeKaraokeVocalsOnly { get; init; }

    /// <summary>
    /// The karaoke pair actions (instrumental on A, original on B). Set only by a
    /// surface that has library rows; a remote row cannot express a pair, so those
    /// surfaces leave these null and the rows stay out of their menu.
    /// </summary>
    public ICommand? PlayKaraoke { get; init; }
    public ICommand? QueueKaraoke { get; init; }
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
    public bool CanMakeKaraokeKeepBacking => MakeKaraokeKeepBacking is not null;
    public bool CanMakeKaraokeVocalsOnly => MakeKaraokeVocalsOnly is not null;

    /// <summary>"Make Karaoke Version" as a submenu of the three stem modes.</summary>
    public bool CanChooseKaraokeMode =>
        MakeKaraoke is not null && MakeKaraokeKeepBacking is not null && MakeKaraokeVocalsOnly is not null;

    /// <summary>"Make Karaoke Version" as one gesture, for surfaces with no mode choice.</summary>
    public bool CanMakeKaraokeDirectly => MakeKaraoke is not null && MakeKaraokeKeepBacking is null;
    public bool CanPlayKaraoke => PlayKaraoke is not null;
    public bool CanQueueKaraoke => QueueKaraoke is not null;
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

    /// <summary>
    /// "Play" and "Add to Queue" as one item per take. Empty on a surface with no
    /// library row, and single-item on a plain track, so both keep the flat item
    /// they had before. Refilled by <c>RefreshTrackMenuVariants</c> whenever the
    /// selected row changes, because availability is read from disk for that row.
    /// </summary>
    public ObservableCollection<TrackVariantItem> PlayVariants { get; } = new();
    public ObservableCollection<TrackVariantItem> QueueVariants { get; } = new();

    /// <summary>
    /// The pair action's takes: the karaoke renders only, so "Play Karaoke
    /// Version" never offers the row's own file. One take keeps the flat item.
    /// </summary>
    public ObservableCollection<TrackVariantItem> PlayKaraokeVariants { get; } = new();

    public bool HasVariantChoice => PlayVariants.Count > 1;
    public bool ShowFlatPlay => Play is not null && !HasVariantChoice;
    public bool ShowVariantPlay => Play is not null && HasVariantChoice;
    public bool ShowFlatQueue => AddToQueue is not null && QueueVariants.Count <= 1;
    public bool ShowVariantQueue => AddToQueue is not null && QueueVariants.Count > 1;
    public bool ShowFlatPlayKaraoke => PlayKaraoke is not null && PlayKaraokeVariants.Count <= 1;
    public bool ShowVariantPlayKaraoke => PlayKaraoke is not null && PlayKaraokeVariants.Count > 1;

    /// <summary>Swap the take lists and re-evaluate which Play/Queue items are shown.</summary>
    internal void SetVariantItems(IReadOnlyList<TrackVariantItem> play,
                                 IReadOnlyList<TrackVariantItem> queue,
                                 IReadOnlyList<TrackVariantItem>? karaoke = null)
    {
        PlayVariants.Clear();
        foreach (var item in play) PlayVariants.Add(item);
        QueueVariants.Clear();
        foreach (var item in queue) QueueVariants.Add(item);
        PlayKaraokeVariants.Clear();
        foreach (var item in karaoke ?? Array.Empty<TrackVariantItem>()) PlayKaraokeVariants.Add(item);

        OnPropertyChanged(nameof(HasVariantChoice));
        OnPropertyChanged(nameof(ShowFlatPlay));
        OnPropertyChanged(nameof(ShowVariantPlay));
        OnPropertyChanged(nameof(ShowFlatQueue));
        OnPropertyChanged(nameof(ShowVariantQueue));
        OnPropertyChanged(nameof(ShowFlatPlayKaraoke));
        OnPropertyChanged(nameof(ShowVariantPlayKaraoke));
    }

    /// <summary>
    /// One menu item per take that exists, in the model's stable order. Pure
    /// composition so the menu the operator sees can be checked without a window:
    /// a take whose file is missing is left out rather than drawn dead.
    /// </summary>
    internal static List<TrackVariantItem> BuildVariantItems(
        VariantAvailability variants, Func<AudioVariant, ICommand> command)
    {
        var primary = variants.Primary;
        var items = new List<TrackVariantItem>(variants.Present.Count);

        // The row's own file, named by what it actually is: on a "Song.vocals.mp3"
        // row that entry reads "Vocals", not "Original", so it agrees with the
        // mark the strip draws for the same file. The command is unchanged - it
        // still resolves AudioVariant.Original, which is this row's own path.
        if (variants.Present.Any(slot => slot.Variant == AudioVariant.Original))
            items.Add(new TrackVariantItem(primary, KaraokeNaming.DisplayName(primary),
                                           VariantGlyphs.For(primary), command(AudioVariant.Original)));

        // Then every other take it actually has, each with its own mark. The
        // primary's slot is skipped: it resolves to the same file as the entry
        // above, so listing it would offer one file twice.
        foreach (var slot in variants.Present)
            if (slot.Variant != AudioVariant.Original && slot.Variant != primary)
                items.Add(new TrackVariantItem(slot.Variant, slot.Label, slot.Icon,
                                               command(slot.Variant)));

        return items;
    }

    /// <summary>
    /// The queue's takes. The queue stores one pair row per song and its mate is
    /// resolved by <c>PlaybackQueueItem.FromKaraokePair</c>, which cannot carry a
    /// named take, so only the original and the karaoke take are offered here. A
    /// keep-backing or vocals row would be a promise the queue cannot keep.
    /// </summary>
    internal static List<TrackVariantItem> BuildQueueVariantItems(
        VariantAvailability variants, Func<AudioVariant, ICommand> command) =>
        BuildVariantItems(variants, command)
            .Where(item => item.Variant is AudioVariant.Original or AudioVariant.Karaoke)
            .ToList();

    /// <summary>
    /// The pair action's takes: every render on disk, never the row's own file.
    /// The labels and marks are <see cref="BuildVariantItems"/>'s, so this menu
    /// reads the same as Play and Add to Queue.
    /// </summary>
    internal static List<TrackVariantItem> BuildKaraokeVariantItems(
        VariantAvailability variants, Func<AudioVariant, ICommand> command) =>
        BuildVariantItems(variants, command)
            .Where(item => item.Variant != AudioVariant.Original)
            .ToList();
}

/// <summary>
/// One "Play" or "Add to Queue" submenu item: the take, its label, and the glyph
/// that stands for it (see <see cref="VariantGlyphs"/>; the design lane owns the
/// characters). The command is carried on the item, the same way
/// <see cref="PlaylistTargetItem"/> does it, so the menu never reaches back up
/// its own visual tree.
/// </summary>
public sealed record TrackVariantItem(AudioVariant Variant, string Header, string Icon, ICommand Command);

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
            PlayKaraoke = PlayKaraokeSelectedCommand,
            QueueKaraoke = QueueKaraokeSelectedCommand,
            AddToQueue = AddToQueueCommand,
            MakeKaraoke = MakeKaraokeInstrumentalCommand,
            MakeKaraokeKeepBacking = MakeKaraokeKeepBackingCommand,
            MakeKaraokeVocalsOnly = MakeKaraokeVocalsOnlyCommand,
            ShowInExplorer = RevealInExplorerCommand,
        };

        RebuildPlaylistTargets(caps);
        InternalPlaylists.CollectionChanged += (_, _) => RebuildPlaylistTargets(caps);
        return caps;
    }

    private void RefreshTrackMenuVariants()
    {
        if (_trackMenu is not { } caps) return;

        var track = SelectedTreeNode?.Track;
        var variants = track?.Variants ?? VariantAvailability.None;

        caps.SetVariantItems(
            TrackMenuCapabilities.BuildVariantItems(variants,
                variant => new RelayCommand(() => PlayVariant(track, variant))),
            TrackMenuCapabilities.BuildQueueVariantItems(variants,
                variant => new RelayCommand(() => QueueVariant(track, variant))),
            TrackMenuCapabilities.BuildKaraokeVariantItems(variants,
                variant => new RelayCommand(() => PlayKaraokeSelected(variant))));
    }

    /// <summary>Add one take of a row to the queue.</summary>
    private void QueueVariant(TrackEntry? track, AudioVariant variant)
    {
        if (track == null) return;

        // The original is today's single-row enqueue, untouched.
        if (variant == AudioVariant.Original) AddToQueueCommand.Execute(null);
        else EnqueueKaraokePair(track);
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
}

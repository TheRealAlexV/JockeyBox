using System.Windows;
using System.Windows.Controls;
using JockeyBox.Library;

namespace JockeyBox.Views;

/// <summary>
/// Move one library track to another folder: a text box holding the destination
/// relative to the library root, plus an optional "artist \ album \ title" preset
/// row that fills that box from tags.
///
/// The dialog is text-only on purpose (no folder browser): the destination must
/// stay inside the library, and a relative path is what the caller persists as
/// <c>TrackEntry.RelativeKey</c>. Validation is
/// <see cref="MediaFileOps.ValidateFolder(string)"/>, shown inline; Enter is OK and
/// Esc is Cancel.
///
/// Nothing here moves a file. The caller passes <see cref="TargetFolder"/> to
/// <see cref="MediaFileOps.MoveTo"/> - or the three preset fields to
/// <see cref="MediaFileOps.MoveToPreset"/> when <see cref="UsePreset"/> is true,
/// which also renames the file to the title.
/// </summary>
public partial class MoveTrackDialog : Window
{
    private bool _filling;

    /// <summary>
    /// Create the dialog.
    /// </summary>
    /// <param name="currentRelativePath">Shown at the top, for context.</param>
    /// <param name="artist">Prefills the preset row.</param>
    /// <param name="album">Prefills the preset row.</param>
    /// <param name="title">Prefills the preset row.</param>
    /// <param name="initialFolder">
    /// Initial folder text; defaults to <see cref="MediaFileOps.SuggestFolderFromTags"/>.
    /// </param>
    public MoveTrackDialog(
        string currentRelativePath,
        string? artist = null,
        string? album = null,
        string? title = null,
        string? initialFolder = null)
    {
        CurrentRelativePath = (currentRelativePath ?? "").Trim();
        InitializeComponent();

        _filling = true;
        CurrentPathLine.Text = CurrentRelativePath;
        ArtistBox.Text = (artist ?? "").Trim();
        AlbumBox.Text = (album ?? "").Trim();
        TitleBox.Text = (title ?? "").Trim();

        var folder = (initialFolder ?? "").Trim();
        if (folder.Length == 0) folder = MediaFileOps.SuggestFolderFromTags(artist, album);
        FolderBox.Text = folder;
        _filling = false;

        Loaded += (_, _) =>
        {
            FolderBox.Focus();
            FolderBox.SelectAll();
            Validate();
        };
    }

    /// <summary>The path shown at the top; not used to build the destination.</summary>
    public string CurrentRelativePath { get; }

    /// <summary>
    /// Destination folder, relative to the library root, as accepted by
    /// <see cref="MediaFileOps.MoveTo"/>. Set on OK; empty means the root.
    /// </summary>
    public string TargetFolder { get; private set; } = "";

    /// <summary>
    /// True when the user built the destination with the preset row. The caller
    /// should then call <see cref="MediaFileOps.MoveToPreset"/> with the three
    /// fields instead of <see cref="MediaFileOps.MoveTo"/>, so the file is renamed
    /// to the title too.
    /// </summary>
    public bool UsePreset { get; private set; }

    /// <summary>Artist typed into the preset row (trimmed). Set on OK.</summary>
    public string PresetArtist { get; private set; } = "";

    /// <summary>Album typed into the preset row (trimmed). Set on OK; may be empty.</summary>
    public string PresetAlbum { get; private set; } = "";

    /// <summary>Title typed into the preset row (trimmed). Set on OK; may be empty.</summary>
    public string PresetTitle { get; private set; } = "";

    private void OnFolderChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling) return;

        // Hand-editing the folder leaves the preset behind: the user now owns the path.
        UsePreset = false;
        Validate();
    }

    private void OnPresetFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling) return;
        if (UsePreset) Validate();
    }

    private void OnUsePreset(object sender, RoutedEventArgs e)
    {
        var artist = ArtistBox.Text.Trim();
        var album = AlbumBox.Text.Trim();

        if (artist.Length == 0)
        {
            ShowMessage("The preset needs an artist: every track lives under an artist folder.", isError: true);
            ArtistBox.Focus();
            return;
        }

        _filling = true;
        FolderBox.Text = MediaFileOps.SuggestFolderFromTags(artist, album);
        _filling = false;

        UsePreset = true;
        Validate();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Validate() is { } error)
        {
            ShowMessage(error, isError: true);
            (UsePreset ? ArtistBox : FolderBox).Focus();
            return;
        }

        TargetFolder = FolderBox.Text.Trim();
        PresetArtist = ArtistBox.Text.Trim();
        PresetAlbum = AlbumBox.Text.Trim();
        PresetTitle = TitleBox.Text.Trim();
        CloseWithResult(true);
    }

    /// <summary>
    /// Check the active input (preset row or folder box) and show the result
    /// inline. Returns the error, or null when the dialog can be accepted.
    /// </summary>
    private string? Validate()
    {
        if (FolderBox is null || ValidationLine is null) return null;

        if (UsePreset && ArtistBox.Text.Trim().Length == 0)
        {
            ShowMessage("The preset needs an artist.", isError: true);
            return "The preset needs an artist.";
        }

        var error = MediaFileOps.ValidateFolder(FolderBox.Text);
        if (error is not null)
        {
            ShowMessage(error, isError: true);
            return error;
        }

        var folder = FolderBox.Text.Trim();
        var destination = folder.Length == 0 ? "the library root" : folder;
        ShowMessage(
            UsePreset
                ? $"Will move to {destination} and rename to the title."
                : $"Will move to {destination}.",
            isError: false);

        return null;
    }

    private void ShowMessage(string message, bool isError)
    {
        ValidationLine.Text = message;
        ValidationLine.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "AccentBBrush" : "TextDimBrush");
    }

    private void CloseWithResult(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}

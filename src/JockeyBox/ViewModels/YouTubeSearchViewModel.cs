using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.YouTube;

namespace JockeyBox.ViewModels;

/// <summary>
/// The "find an upload" window: a YouTube search with an audition button on
/// every row.
///
/// It exists because Spotify only says the song exists - it never says which
/// upload to fetch - so a Spotify row with no <see cref="Sources.SourceTrack.VideoId"/>
/// used to dead-end on "find versions first". Both that refusal and the row
/// menu's Find on YouTube open this window with the row's artist and title
/// already typed in, so the operator lands on results instead of an empty box.
///
/// Auditioning goes through the app's one <see cref="StreamPreviewTransport"/>,
/// the same pipe the tabs preview with: one audio source at a time, nothing
/// written to disk, and the deck stops it if it starts playing.
/// </summary>
public partial class YouTubeSearchViewModel : ObservableObject
{
    /// <summary>Enough to cover covers, live takes, lyric videos and remixes without a second search.</summary>
    private const int ResultCount = 25;

    private readonly AppSettings _settings;
    private readonly StreamPreviewTransport _stream;

    /// <summary>Set only when the window can complete the loop by choosing the upload.</summary>
    private readonly Action<YtEntry>? _pick;

    /// <summary>The URL this window started, so closing it never stops someone else's preview.</summary>
    private string? _previewUrl;

    /// <summary>Search token: a slow answer for an old query must not overwrite a new one.</summary>
    private int _generation;

    public YouTubeSearchViewModel(
        AppSettings settings,
        StreamPreviewTransport stream,
        string query,
        string? targetLabel = null,
        Action<YtEntry>? pick = null)
    {
        _settings = settings;
        _stream = stream;
        _pick = pick;
        _query = query;
        TargetLabel = targetLabel ?? "";

        // The strip is the shared control the tabs use, on this window's own
        // transport: one seek path for the whole app, no second slider here.
        Strip = new PreviewBarViewModel(stream);

        _stream.StateChanged += OnTransportChanged;

        // The player says why a preview was refused (no yt-dlp, extractor change,
        // age gate) on StatusChanged; without this the window looked like a click
        // that did nothing. Both tabs already show these words.
        _stream.StatusChanged += OnStatusChanged;

        RefreshTransport();
    }

    public ObservableCollection<YtEntry> Results { get; } = new();

    /// <summary>
    /// What the audition strip binds to: the shared preview strip, driven by the
    /// same <see cref="StreamPreviewTransport"/> the tabs preview through. A
    /// seek here and a seek on a tab strip are the same code.
    /// </summary>
    public PreviewBarViewModel Strip { get; }

    /// <summary>What the search is for, e.g. "Daft Punk - Around the World".</summary>
    public string TargetLabel { get; }

    /// <summary>True when this window was opened for a row it can set the upload on.</summary>
    public bool CanPick => _pick != null;

    public bool HasResults => Results.Count > 0;

    /// <summary>Nothing found and nothing running: the list shows a line instead of blank space.</summary>
    public bool IsEmpty => !IsSearching && Results.Count == 0;

    public string EmptyText => string.IsNullOrWhiteSpace(Query)
        ? "Type what to look for, then press Enter."
        : $"Nothing on YouTube for \u201c{Query.Trim()}\u201d.";

    [ObservableProperty] private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isSearching;

    [ObservableProperty] private YtEntry? _selectedResult;

    /// <summary>The video this window is auditioning; empty when it is not playing anything.</summary>
    [ObservableProperty] private string _playingVideoId = "";

    [ObservableProperty] private string _previewTitle = "";

    [ObservableProperty] private string _previewClock = "";

    [ObservableProperty] private string _playGlyph = "\u25B6";

    [ObservableProperty] private bool _hasPreview;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    /// <summary>The strip's header follows the title the window is auditioning.</summary>
    partial void OnPreviewTitleChanged(string value) => Strip.Title = value;

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>
    /// Search for <see cref="Query"/> and list the uploads. The window calls
    /// this once on open, so the operator never sees an empty result pane for a
    /// row they already pointed at.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = Query.Trim();
        if (query.Length == 0)
        {
            StatusText = "Type something to search for.";
            return;
        }

        var generation = ++_generation;
        IsSearching = true;
        StatusText = $"Looking for \u201c{query}\u201d on YouTube\u2026";

        try
        {
            // YouTubeApi drops live streams and absurd lengths, so a row here is
            // always something that can be auditioned and downloaded.
            var found = await YouTubeApi.SearchAsync(query, ResultCount, _settings, CancellationToken.None)
                .ConfigureAwait(true);
            if (generation != _generation) return;

            Results.Clear();
            foreach (var entry in found) Results.Add(entry);
            SelectedResult = Results.FirstOrDefault();

            StatusText = found.Count == 0
                ? $"Nothing on YouTube for \u201c{query}\u201d."
                : $"{found.Count} uploads for \u201c{query}\u201d.";
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            StatusText = $"Could not search YouTube: {ex.Message}";
        }
        finally
        {
            if (generation == _generation)
            {
                IsSearching = false;
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>Audition one upload. Clicking the row that is already playing stops it.</summary>
    [RelayCommand]
    private void Preview(YtEntry? entry)
    {
        if (entry is null) return;

        if (string.IsNullOrWhiteSpace(entry.VideoId))
        {
            StatusText = "This row has no YouTube upload id \u2014 search again.";
            return;
        }

        var url = Url(entry.VideoId);

        // Stop only a stream this player still owns: the transport is shared with
        // both tabs and the deck, so it can be stopped from outside this window
        // while PlayingVideoId still names the last row clicked here. Left as it
        // was, that stale flag turned the next click into a silent Stop.
        var ownsStream = string.Equals(_stream.CurrentUrl, url, StringComparison.OrdinalIgnoreCase)
                         && (_stream.IsPlaying || _stream.IsLoading);
        if (entry.VideoId == PlayingVideoId && ownsStream)
        {
            StopPreview();
            return;
        }

        if (!_stream.CanStream)
        {
            StatusText = _stream.UnavailableText;
            return;
        }

        _previewUrl = Url(entry.VideoId);
        _stream.Play(_previewUrl, entry.ToString());
        PlayingVideoId = entry.VideoId;
        PreviewTitle = entry.Title;
        StatusText = $"Previewing \u201c{entry.Title}\u201d ({entry.Uploader}) \u2014 streaming, nothing is saved.";
        RefreshTransport();
    }

    [RelayCommand]
    private void TogglePreview()
    {
        if (!HasPreview) return;
        _stream.SetPlaying(!_stream.IsPlaying);
        RefreshTransport();
    }

    [RelayCommand]
    private void StopPreview()
    {
        if (_previewUrl is null) return;
        _stream.StopIfUrl(_previewUrl);
        _previewUrl = null;
        PlayingVideoId = "";
        PreviewTitle = "";
        RefreshTransport();
    }

    /// <summary>
    /// Make this upload the row's YouTube version - the same state the Spotify
    /// versions panel sets, so the row can be auditioned and queued from the tab
    /// afterwards. Deliberately no download: auditioning is not a commitment.
    /// </summary>
    [RelayCommand]
    private void Pick(YtEntry? entry)
    {
        if (entry is null || _pick is null) return;
        _pick(entry);
        StatusText = $"Using \u201c{entry.Title}\u201d ({entry.Uploader}) for this track.";
    }

    /// <summary>The one place Process.Start survives: hand a candidate to the browser.</summary>
    [RelayCommand]
    private void OpenInBrowser(YtEntry? entry)
    {
        if (entry is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Url(entry.VideoId)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the browser: {ex.Message}";
        }
    }

    /// <summary>
    /// Called when the window closes: stop the preview this window started and
    /// drop the transport handler. StopIfUrl is not politeness - it is what stops
    /// a close from cutting off a preview the operator started in a tab.
    /// </summary>
    public void Shutdown()
    {
        _stream.StateChanged -= OnTransportChanged;
        _stream.StatusChanged -= OnStatusChanged;
        _generation++;
        if (_previewUrl is not null) _stream.StopIfUrl(_previewUrl);
        _previewUrl = null;
    }

    private static string Url(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    private void OnTransportChanged(object? sender, EventArgs e)
    {
        // The player reports from its own thread; the strip is bound to the UI.
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(RefreshTransport));
            return;
        }

        RefreshTransport();
    }

    /// <summary>
    /// The player's own words for this preview, on the strip: "Resolving stream…",
    /// a download percentage, or the reason a resolve was refused. A blank note is
    /// the player clearing its own line, so it never wipes a search message.
    /// </summary>
    private void OnStatusChanged(object? sender, string message)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(() => OnStatusChanged(sender, message)));
            return;
        }

        if (!string.IsNullOrWhiteSpace(message)) StatusText = message;
    }

    private void RefreshTransport()
    {
        var mine = _previewUrl is not null && _stream.CurrentUrl == _previewUrl;
        HasPreview = mine && _stream.IsAvailable;

        if (!HasPreview)
        {
            PlayGlyph = "\u25B6";
            PreviewClock = "";
            return;
        }

        PlayGlyph = _stream.IsLoading ? "\u2026" : _stream.IsPlaying ? "\u23F8" : "\u25B6";

        var position = TimeSpan.FromSeconds(Math.Max(0, _stream.PositionSeconds));
        var length = TimeSpan.FromSeconds(Math.Max(0, _stream.DurationSeconds));
        PreviewClock = length > TimeSpan.Zero
            ? $"{position:m\\:ss} / {length:m\\:ss}"
            : $"{position:m\\:ss}";
    }
}

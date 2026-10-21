using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JockeyBox.Library;
using JockeyBox.Lyrics;
using JockeyBox.Separation;
using JockeyBox.Settings;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Views;

public partial class SettingsWindow : Window
{
    private static readonly string[] FormatPresets = { "FLAC", "MP3", "WAV" };

    private readonly AppSettings _settings;
    private readonly SpotifyOAuthService _oauth;

    /// <summary>
    /// Library rows come from the main view model (this window never owns them).
    /// Optional so the dialog still works when opened without a library.
    /// </summary>
    private readonly Func<IReadOnlyList<TrackEntry>>? _tracks;
    private readonly PlaylistStore? _playlists;
    private bool _libraryToolsBusy;

    public SettingsWindow(AppSettings settings, Func<IReadOnlyList<TrackEntry>>? tracks = null,
                          PlaylistStore? playlists = null)
    {
        InitializeComponent();
        _settings = settings;
        _tracks = tracks;
        _playlists = playlists;
        _oauth = new SpotifyOAuthService(settings);

        MusicDirBox.Text = settings.MusicDirectory;
        KaraokeDirBox.Text = settings.KaraokeDirectory;

        foreach (var f in FormatPresets) FormatBox.Items.Add(f);
        FormatBox.SelectedItem = FormatPresets.Contains(settings.SeparationOutputFormat)
            ? settings.SeparationOutputFormat : "FLAC";
        PythonBox.Text = settings.PythonPath;
        GpuCheck.IsChecked = settings.SeparationUseGpu;
        UpdateEngineState();

        ApiUrlBox.Text = settings.LyricsApiBaseUrl;
        OffsetBox.Text = settings.LyricsOffsetMs.ToString("F0");
        SpotifyClientIdBox.Text = settings.SpotifyClientIdOverride;
        UpdateSpotifyStatus();

        LoadDownloadSettings(settings);
    }

    /// <summary>Fill the download and tool boxes from settings (values live in item tags).</summary>
    private void LoadDownloadSettings(AppSettings settings)
    {
        SelectByTag(LayoutPresetBox, settings.DownloadLayoutPreset, "artist-album");
        LayoutTemplateBox.Text = settings.DownloadLayoutTemplate;
        SelectByTag(BitrateBox, settings.DownloadBitrateKbps.ToString(), "320");

        for (var i = 1; i <= 4; i++)
            ConcurrencyBox.Items.Add(new ComboBoxItem { Content = i.ToString(), Tag = i.ToString() });
        SelectByTag(ConcurrencyBox, settings.DownloadConcurrency.ToString(), "2");

        SelectByTag(CookiesBox, settings.DownloadCookiesBrowser, "");
        SkipExistingCheck.IsChecked = settings.DownloadSkipExisting;
        AutoKaraokeCheck.IsChecked = settings.DownloadAutoKaraoke;

        foreach (var mb in new[] { 512, 1024, 2048, 4096 })
            CacheBox.Items.Add(new ComboBoxItem { Content = mb.ToString(), Tag = mb.ToString() });
        SelectByTag(CacheBox, settings.StreamCacheLimitMb.ToString(), "2048");

        foreach (var (label, minutes) in new[] { ("Off", 0), ("Every 15 min", 15), ("Every 60 min", 60), ("Every 6 hours", 360) })
            AutoSyncBox.Items.Add(new ComboBoxItem { Content = label, Tag = minutes.ToString() });
        SelectByTag(AutoSyncBox, settings.YouTubeAutoSyncMinutes.ToString(), "0");

        YtDlpPathBox.Text = settings.YtDlpPath;
        FfmpegPathBox.Text = settings.FfmpegPath;
    }

    private static void SelectByTag(ComboBox box, string? tag, string fallback)
    {
        var wanted = tag ?? "";
        var match = box.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, wanted, StringComparison.OrdinalIgnoreCase))
            ?? box.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string?)i.Tag, fallback, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = match ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void UpdateEngineState() =>
        EngineStateText.Text = SeparationEngine.IsInstalled
            ? "\u2713 engine installed" : "engine not installed yet";

    private void UpdateSpotifyStatus()
    {
        if (_oauth.IsLoggedIn)
        {
            if (_oauth.HasPlaylistScopes)
                SpotifyStatus.Text = "Connected - playlist access granted";
            else if (string.IsNullOrWhiteSpace(_settings.SpotifyClientIdOverride))
                SpotifyStatus.Text = "Connected - needs your own Spotify app";
            else
                SpotifyStatus.Text = "Connected - playlist access not granted";
        }
        else if (!string.IsNullOrWhiteSpace(_settings.SpotifyClientId))
            SpotifyStatus.Text = "\u2713 Using client credentials (legacy)";
        else
            SpotifyStatus.Text = "Not connected";
    }

    /// <summary>
    /// Open the shared Spotify setup dialog. The dialog writes the client id
    /// straight to settings, so mirror it back into the text box afterwards -
    /// otherwise this window's Save would restore the old value.
    /// </summary>
    private void OnSpotifySetupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SpotifySetupDialog(_settings, _oauth) { Owner = this };
        dialog.ShowDialog();
        SpotifyClientIdBox.Text = _settings.SpotifyClientIdOverride;
        UpdateSpotifyStatus();
    }

    private async void OnSpotifyLoginClick(object sender, RoutedEventArgs e)
    {
        SpotifyLoginButton.IsEnabled = false;
        SpotifyStatus.Text = "Opening browser for login\u2026";

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            await _oauth.LoginAsync(cts.Token);
            UpdateSpotifyStatus();
        }
        catch (OperationCanceledException)
        {
            SpotifyStatus.Text = "Login timed out or was cancelled";
        }
        catch (Exception ex)
        {
            SpotifyStatus.Text = $"Login failed: {ex.Message}";
        }
        finally
        {
            SpotifyLoginButton.IsEnabled = true;
        }
    }

    private void OnBrowseMusic(object sender, RoutedEventArgs e) => BrowseInto(MusicDirBox, "Select your Music folder (originals with vocals)");

    private void OnBrowseKaraoke(object sender, RoutedEventArgs e) => BrowseInto(KaraokeDirBox, "Select the Karaoke folder (generated instrumentals)");

    private void BrowseInto(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void OnInstallEngine(object sender, RoutedEventArgs e)
    {
        _settings.PythonPath = PythonBox.Text.Trim();
        _settings.SeparationUseGpu = GpuCheck.IsChecked == true;
        _settings.Save();

        var setup = new SetupWindow(_settings) { Owner = this };
        setup.ShowDialog();
        UpdateEngineState();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var apiUrl = ApiUrlBox.Text.Trim();
        if (apiUrl.Length > 0
            && (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            MessageBox.Show(this,
                "Lyrics API base URL must be a full http:// or https:// address (e.g. http://10.0.0.5:8091).",
                "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var music = MusicDirBox.Text.Trim();
        var karaoke = KaraokeDirBox.Text.Trim();
        if (music.Length > 0 && karaoke.Length > 0
            && string.Equals(System.IO.Path.GetFullPath(music), System.IO.Path.GetFullPath(karaoke),
                             StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Music and Karaoke folders must be different directories.",
                "Invalid folders", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.MusicDirectory = music;
        _settings.KaraokeDirectory = karaoke;
        _settings.SeparationOutputFormat = (FormatBox.SelectedItem as string) ?? "FLAC";
        _settings.PythonPath = PythonBox.Text.Trim();
        _settings.SeparationUseGpu = GpuCheck.IsChecked == true;
        _settings.LyricsApiBaseUrl = apiUrl;
        if (double.TryParse(OffsetBox.Text, out var offset))
            _settings.LyricsOffsetMs = Math.Clamp(offset, -10000, 10000);
        _settings.SpotifyClientIdOverride = SpotifyClientIdBox.Text.Trim();

        _settings.DownloadLayoutPreset = TagOf(LayoutPresetBox);
        _settings.DownloadLayoutTemplate = LayoutTemplateBox.Text.Trim();
        if (int.TryParse(TagOf(BitrateBox), out var bitrate)) _settings.DownloadBitrateKbps = bitrate;
        if (int.TryParse(TagOf(ConcurrencyBox), out var concurrency))
            _settings.DownloadConcurrency = Math.Clamp(concurrency, 1, 4);
        _settings.DownloadCookiesBrowser = TagOf(CookiesBox);
        _settings.DownloadSkipExisting = SkipExistingCheck.IsChecked == true;
        _settings.DownloadAutoKaraoke = AutoKaraokeCheck.IsChecked == true;
        if (int.TryParse(TagOf(CacheBox), out var cacheMb)) _settings.StreamCacheLimitMb = cacheMb;
        if (int.TryParse(TagOf(AutoSyncBox), out var autoSync)) _settings.YouTubeAutoSyncMinutes = autoSync;
        _settings.YtDlpPath = YtDlpPathBox.Text.Trim();
        _settings.FfmpegPath = FfmpegPathBox.Text.Trim();

        _settings.Save();
        DialogResult = true;
    }

    // ---- tools ------------------------------------------------------------

    private async void OnInstallTools(object sender, RoutedEventArgs e) =>
        await RunToolTaskAsync(ToolBootstrap.EnsureAllAsync, "Checking yt-dlp and ffmpeg\u2026",
            "Tools are ready.", "yt-dlp is not available \u2014 see the log.");

    private async void OnUpdateYtDlp(object sender, RoutedEventArgs e) =>
        await RunToolTaskAsync(ToolBootstrap.UpdateYtDlpAsync, "Updating yt-dlp\u2026",
            "yt-dlp updated.", "yt-dlp update failed \u2014 see the log.");

    /// <summary>
    /// The queue window owns the live tool state; this is a convenience copy for
    /// someone already in Settings. Path overrides typed here are used as-is.
    /// </summary>
    private async Task RunToolTaskAsync(
        Func<AppSettings, IProgress<string>, CancellationToken, Task<bool>> work,
        string starting, string success, string failure)
    {
        _settings.YtDlpPath = YtDlpPathBox.Text.Trim();
        _settings.FfmpegPath = FfmpegPathBox.Text.Trim();
        _settings.Save();

        InstallToolsButton.IsEnabled = false;
        UpdateYtDlpButton.IsEnabled = false;
        ToolsStatusText.Text = starting;

        try
        {
            var progress = new Progress<string>(message => ToolsStatusText.Text = message);
            var ok = await work(_settings, progress, CancellationToken.None);
            ToolsStatusText.Text = ok ? success : failure;
        }
        catch (Exception ex)
        {
            ToolsStatusText.Text = $"Tool setup failed: {ex.Message}";
        }
        finally
        {
            InstallToolsButton.IsEnabled = true;
            UpdateYtDlpButton.IsEnabled = true;
        }
    }

    // ---- library tools ----------------------------------------------------

    private async void OnFindDuplicatesClick(object sender, RoutedEventArgs e)
    {
        if (_libraryToolsBusy) return;
        var tracks = SnapshotLibrary();
        if (tracks == null) return;

        DuplicateGroups? result = null;
        await RunLibraryToolAsync($"Scanning {tracks.Count} track(s) for duplicates\u2026",
            async ct =>
            {
                result = await Task.Run(() => LibraryTools.FindDuplicates(tracks, ct), ct);
            });

        if (result == null) return;

        if (result.IsEmpty)
        {
            LibraryToolsStatusText.Text = $"No duplicates across {result.MusicFiles} music file(s).";
            return;
        }

        LibraryToolsStatusText.Text =
            $"{result.Groups.Count} group(s) \u00b7 {result.DuplicateFileCount} of {result.MusicFiles} " +
            $"file(s) \u00b7 {FormatSize(result.ReclaimableBytes)} reclaimable.";
        ShowDuplicateGroups(result);
    }

    private async void OnImportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_libraryToolsBusy) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import an Exportify playlist (CSV)",
            Filter = "Exportify CSV (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name)) name = "Imported playlist";

        List<SourceTrack>? imported = null;
        var skipped = 0;
        await RunLibraryToolAsync($"Reading {Path.GetFileName(path)}\u2026", async ct =>
        {
            var parsed = await Task.Run(() =>
            {
                var rows = LibraryTools.ImportExportifyCsv(path, out var skip);
                return (Rows: rows, Skipped: skip);
            }, ct);

            imported = parsed.Rows;
            skipped = parsed.Skipped;
        });

        if (imported == null) return;

        if (imported.Count == 0)
        {
            LibraryToolsStatusText.Text =
                $"No usable rows in {Path.GetFileName(path)} ({skipped} skipped).";
            return;
        }

        // Share the app's live store when it is available: a second instance
        // would write the same file and be clobbered by the next tab save.
        var store = _playlists ?? new PlaylistStore();
        var playlist = store.FindByName(name) ?? store.Create(name, MediaSource.Spotify);
        var added = store.AddTracks(playlist, imported);
        store.Save();

        Log.Info($"Library tools: imported {imported.Count} track(s) from {path} " +
                 $"into \"{playlist.Name}\" ({added} new, {skipped} skipped).");

        LibraryToolsStatusText.Text =
            $"Imported {imported.Count} track(s) into \"{playlist.Name}\" " +
            $"({added} new, {skipped} skipped). Restart to see it in the source tabs.";
    }

    private async void OnExportJsonClick(object sender, RoutedEventArgs e)
    {
        if (_libraryToolsBusy) return;
        var tracks = SnapshotLibrary();
        if (tracks == null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the library as JSON",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            AddExtension = true,
            FileName = $"jockeybox-library-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var written = -1;
        await RunLibraryToolAsync("Writing library export\u2026", async ct =>
        {
            // The store reads the playlists file synchronously; keep that on the
            // UI thread and only the document build/write goes async.
            var store = new PlaylistStore();
            written = await LibraryTools.ExportLibraryJsonAsync(path, tracks, store, ct);
        });

        if (written < 0) return;
        LibraryToolsStatusText.Text = $"Exported {written} track(s) to {path}";
        Log.Info($"Library tools: exported {written} track(s) to {path}.");
    }

    /// <summary>Rows for the tools, or null after reporting that there is nothing to work on.</summary>
    private List<TrackEntry>? SnapshotLibrary()
    {
        var tracks = _tracks?.Invoke();
        if (tracks is { Count: > 0 }) return tracks.ToList();

        LibraryToolsStatusText.Text =
            "No library loaded yet \u2014 set the folders above and let the scan finish.";
        return null;
    }

    private async Task RunLibraryToolAsync(string starting, Func<CancellationToken, Task> work)
    {
        if (_libraryToolsBusy) return;

        _libraryToolsBusy = true;
        SetLibraryToolsBusy(true);
        LibraryToolsStatusText.Text = starting;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            LibraryToolsStatusText.Text = "Library tool cancelled or timed out.";
        }
        catch (Exception ex)
        {
            Log.Error("Library tool failed.", ex);
            LibraryToolsStatusText.Text = $"Library tool failed: {ex.Message}";
        }
        finally
        {
            _libraryToolsBusy = false;
            SetLibraryToolsBusy(false);
        }
    }

    private void SetLibraryToolsBusy(bool busy)
    {
        FindDuplicatesButton.IsEnabled = !busy;
        ImportCsvButton.IsEnabled = !busy;
        ExportJsonButton.IsEnabled = !busy;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        var mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0.0} MB";
    }

    private Brush LookupBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush
        ?? Application.Current?.TryFindResource(key) as Brush
        ?? fallback;

    /// <summary>
    /// Read-only duplicate report in the app's dark card style. It lists the
    /// sets and their files; it offers no action, because this tool never
    /// touches the audio files.
    /// </summary>
    private void ShowDuplicateGroups(DuplicateGroups result)
    {
        var window = new Window
        {
            Title = "Duplicate tracks",
            Owner = this,
            Width = 640,
            Height = 560,
            MinWidth = 460,
            MinHeight = 320,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = LookupBrush("WindowBrush", Brushes.Black),
        };

        var root = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(2, 0, 2, 10) };
        header.Children.Add(new TextBlock
        {
            Text = "DUPLICATES",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = LookupBrush("AccentABrush", Brushes.Cyan),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{result.Groups.Count} group(s) \u00b7 {result.DuplicateFileCount} of " +
                   $"{result.MusicFiles} file(s) \u00b7 {FormatSize(result.ReclaimableBytes)} reclaimable",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11,
            Foreground = LookupBrush("TextDimBrush", Brushes.Gray),
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildDuplicateList(result),
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(2, 10, 2, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = "Reporting only \u2014 nothing was moved, renamed or deleted.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = LookupBrush("TextDimBrush", Brushes.Gray),
        };
        Grid.SetColumn(hint, 0);
        footer.Children.Add(hint);

        var close = new Button { Content = "Close", Width = 90, IsCancel = true };
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        window.Content = root;
        window.ShowDialog();
    }

    private StackPanel BuildDuplicateList(DuplicateGroups result)
    {
        var panel = new StackPanel();
        var panelBrush = LookupBrush("PanelBrush", Brushes.Black);
        var borderBrush = LookupBrush("BorderBrush", Brushes.DimGray);
        var textBrush = LookupBrush("TextBrush", Brushes.White);
        var dimBrush = LookupBrush("TextDimBrush", Brushes.Gray);
        var warnBrush = LookupBrush("AccentBBrush", Brushes.IndianRed);

        foreach (var group in result.Groups)
        {
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock
            {
                Text = group.DisplayName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = group.Key,
            });
            inner.Children.Add(new TextBlock
            {
                Text = $"{group.Files.Count} files \u00b7 matched on {group.MatchedBy} \u00b7 " +
                       $"{FormatSize(group.TotalBytes)}",
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 4),
                Foreground = dimBrush,
            });

            foreach (var file in group.Files)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };
                var size = new TextBlock
                {
                    Text = file.Exists ? FormatSize(file.SizeBytes) : "missing",
                    FontSize = 10,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = file.Exists ? dimBrush : warnBrush,
                };
                DockPanel.SetDock(size, Dock.Right);
                row.Children.Add(size);

                row.Children.Add(new TextBlock
                {
                    Text = file.Path,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = file.Path,
                    Foreground = file.Exists ? textBrush : warnBrush,
                });
                inner.Children.Add(row);
            }

            panel.Children.Add(new Border
            {
                Background = panelBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = inner,
            });
        }

        return panel;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

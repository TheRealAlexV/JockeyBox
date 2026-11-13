using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using JockeyBox.Downloads;
using JockeyBox.Tools;

namespace JockeyBox.Activity;

/// <summary>
/// The app-wide list of long-running jobs: downloads, karaoke separation, and
/// (later) playlist loads or library scans, plus a capped window on the app log.
/// Producers may call from any thread; every collection and row write is
/// marshalled onto the WPF dispatcher, so a UI can bind to <see cref="Rows"/>
/// and <see cref="RecentLog"/> without knowing which subsystem reported.
/// </summary>
public sealed partial class ActivityFeed : ObservableObject
{
    /// <summary>
    /// How many log lines the console keeps. A download emits a handful of
    /// lines per second, and a karaoke render adds queue/created/failed lines
    /// that the operator needs to find afterwards; 500 covers a working session
    /// including several karaoke jobs, stays cheap to scroll, and never grows
    /// without bound.
    /// </summary>
    private const int DefaultLogCapacity = 500;

    /// <summary>Process-wide feed; the app has exactly one status console.</summary>
    public static ActivityFeed Shared { get; } = CreateShared();

    private readonly ObservableCollection<ActivityRow> _rows = new();
    private readonly int _logCapacity;
    private bool _logAttached;

    public ActivityFeed(int logCapacity = DefaultLogCapacity)
    {
        _logCapacity = Math.Max(1, logCapacity);
        _rows.CollectionChanged += OnRowsChanged;
    }

    /// <summary>Live rows, newest last. Bind an ItemsControl to this.</summary>
    public ObservableCollection<ActivityRow> Rows => _rows;

    /// <summary>
    /// Recent log lines (oldest first, full "timestamp [LEVEL] text" strings).
    /// Capped at <see cref="LogCapacity"/>; oldest lines are dropped. Keep it in
    /// its own view from <see cref="Rows"/>: log lines are frequent and are
    /// evidence, not jobs.
    /// </summary>
    public ObservableCollection<string> RecentLog { get; } = new();

    /// <summary>Maximum number of lines <see cref="RecentLog"/> holds.</summary>
    public int LogCapacity => _logCapacity;

    /// <summary>
    /// The job row the operator is inspecting, or null. Set by the console when a
    /// row is clicked or arrowed to; the detail pane and its Copy button read it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow), nameof(SelectedRowDetail))]
    private ActivityRow? _selectedRow;

    /// <summary>True when a row is selected, so the detail pane can show itself.</summary>
    public bool HasSelectedRow => SelectedRow is not null;

    /// <summary>The detail pane's text for the selected row.</summary>
    public string SelectedRowDetail => DescribeRow(SelectedRow);

    /// <summary>
    /// The log line the operator selected. Log lines are clipped horizontally, so
    /// selection exists to name the line a Copy button will take in full.
    /// </summary>
    [ObservableProperty]
    private string? _selectedLogLine;

    /// <summary>
    /// Multi-line text for the detail pane, and what its Copy button puts on the
    /// clipboard: every field the operator may need to quote in a bug report.
    /// </summary>
    public static string DescribeRow(ActivityRow? row)
    {
        if (row is null) return "";
        var lines = new List<string>
        {
            $"Id:          {row.Id}",
            $"Kind:        {row.Kind}",
            $"Source:      {(string.IsNullOrEmpty(row.Source) ? "(unknown)" : row.Source)}",
            $"Title:       {row.Title}",
            $"Started:     {row.Started:yyyy-MM-dd HH:mm:ss}",
            $"Finished:    {(row.Finished is { } f ? f.ToString("yyyy-MM-dd HH:mm:ss") : "(running)")}",
            $"State:       {row.StageText}",
            $"Percent:     {(row.Percent is { } p ? $"{p:0}%" : "(none)")}",
            $"Detail:      {(string.IsNullOrWhiteSpace(row.Detail) ? "(none)" : row.Detail)}",
            $"Output:      {(string.IsNullOrWhiteSpace(row.OutputPath) ? "(none)" : row.OutputPath)}",
            $"Error:       {(string.IsNullOrWhiteSpace(row.Error) ? "(none)" : row.Error)}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Rows that have not reached a terminal state yet.</summary>
    public int ActiveCount => _rows.Count(r => !r.IsFinished);

    /// <summary>True while anything is still running; handy for a status indicator.</summary>
    public bool IsBusy => ActiveCount > 0;

    /// <summary>
    /// Report a new job. Replaces any finished row that carried the same id, so
    /// a retry does not leave a stale duplicate behind. Returns immediately with
    /// the row; callers keep the reference and pass it to <see cref="Update"/>.
    /// </summary>
    public ActivityRow Start(
        ActivityKind kind,
        string id,
        string title,
        bool canCancel = false,
        Action? cancel = null,
        string source = "",
        string? outputPath = null)
    {
        var row = new ActivityRow
        {
            Id = id,
            Kind = kind,
            Title = title,
            CanCancel = canCancel && cancel is not null,
            CancelAction = cancel,
            Source = source,
            OutputPath = outputPath,
        };

        OnUi(() =>
        {
            for (var i = _rows.Count - 1; i >= 0; i--)
                if (_rows[i].Id == id && _rows[i].IsFinished) _rows.RemoveAt(i);
            _rows.Add(row);
        });

        return row;
    }

    /// <summary>Push state/progress for a row. Safe from any thread; null row is ignored.</summary>
    public void Update(
        ActivityRow? row,
        DownloadState state,
        double? percent = null,
        string? detail = null,
        string? error = null)
    {
        if (row is null) return;

        OnUi(() =>
        {
            if (row.State != state) row.State = state;
            if (percent is { } p && row.Percent != p) row.Percent = p;
            if (detail != row.Detail) row.Detail = detail;
            if (error is not null && error != row.Error) row.Error = error;
            row.Finished = row.IsFinished ? row.Finished ?? DateTimeOffset.Now : null;
        });
    }

    /// <summary>Terminal update: done/failed/cancelled.</summary>
    public void Finish(
        ActivityRow? row,
        DownloadState state = DownloadState.Done,
        string? detail = null,
        string? error = null)
        => Update(row, state, null, detail, error);

    /// <summary>Look up a row by id. UI thread only (producers hold their own reference).</summary>
    public ActivityRow? Find(string id) => _rows.FirstOrDefault(r => r.Id == id);

    /// <summary>Ask the producer of <paramref name="id"/> to stop. Returns false if it cannot.</summary>
    public bool Cancel(string id)
    {
        var row = Find(id);
        if (row?.CancelAction is null || row.IsFinished) return false;
        row.CancelAction();
        return true;
    }

    /// <summary>Drop every finished row.</summary>
    public void ClearFinished() => OnUi(() =>
    {
        for (var i = _rows.Count - 1; i >= 0; i--)
            if (_rows[i].IsFinished) _rows.RemoveAt(i);
    });

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ActivityRow row in e.OldItems) row.PropertyChanged -= OnRowPropertyChanged;
        if (e.NewItems is not null)
            foreach (ActivityRow row in e.NewItems) row.PropertyChanged += OnRowPropertyChanged;
        if (SelectedRow is { } selected && !_rows.Contains(selected)) SelectedRow = null;
        RaiseCounts();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityRow.IsFinished)) RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(IsBusy));
    }

    private static ActivityFeed CreateShared()
    {
        var feed = new ActivityFeed();
        feed.AttachLog();
        return feed;
    }

    /// <summary>
    /// Start mirroring <see cref="Log.LineWritten"/> into <see cref="RecentLog"/>.
    /// Idempotent; <see cref="Shared"/> is attached at construction.
    /// </summary>
    public void AttachLog()
    {
        if (_logAttached) return;
        _logAttached = true;
        Log.LineWritten += OnLogLine;
    }

    /// <summary>Stop mirroring the log; safe to call on pane teardown.</summary>
    public void DetachLog()
    {
        if (!_logAttached) return;
        _logAttached = false;
        Log.LineWritten -= OnLogLine;
    }

    // Runs on whichever thread logged. It only queues UI work, so the logger is
    // never blocked, and the append can never throw back into Log.Write (which
    // also wraps its own event invoke in a try/catch).
    private void OnLogLine(string line) => OnUi(() =>
    {
        RecentLog.Add(line);
        while (RecentLog.Count > _logCapacity) RecentLog.RemoveAt(0);
    });

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.InvokeAsync(action);
    }
}

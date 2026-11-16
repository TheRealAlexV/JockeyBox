using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using JockeyBox.Lyrics;
using JockeyBox.Tools;

namespace JockeyBox.Metadata;

/// <summary>
/// What changed in <see cref="TrackMetadataStore"/>; carried by the
/// <see cref="TrackMetadataStore.Changed"/> event so a listener can ignore
/// changes for tracks it is not showing.
/// </summary>
public enum TrackMetadataChange
{
    /// <summary>The record was created by <c>GetOrCreate</c> or by an import.</summary>
    Created,
    /// <summary>Base offset, source id or follow mode changed.</summary>
    Property,
    /// <summary>Title/artist identity was (re)recorded.</summary>
    Identity,
    /// <summary>A section was added, removed or edited.</summary>
    Sections,
    /// <summary>The whole record was removed.</summary>
    Removed,
}

public sealed class TrackMetadataChangedEventArgs : EventArgs
{
    public TrackMetadataChangedEventArgs(string key, TrackMetadataChange change)
    {
        Key = key;
        Change = change;
    }

    /// <summary>Normalised <see cref="TrackMetadata.Key"/>.</summary>
    public string Key { get; }

    public TrackMetadataChange Change { get; }
}

/// <summary>
/// One track's saved lyric metadata. Persisted as an element of
/// <c>track-metadata.json</c>; also the live object the store hands out, so the
/// UI may read a property and pass it straight to a binding-free code path.
/// <para>
/// Mutable and <b>not</b> thread-safe on its own: treat an instance returned by
/// the store as a snapshot for reading, and go through the store's setters for
/// every write (they take the lock, mark the document dirty and raise
/// <see cref="TrackMetadataStore.Changed"/>).
/// </para>
/// </summary>
public sealed class TrackMetadata
{
    /// <summary>
    /// Normalised library key (see <see cref="TrackMetadataStore.NormalizeKey"/>):
    /// lowercased, <c>/</c>-folded, extension-stripped relative path. This is the
    /// dictionary identity and survives a library rebuild.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>Track title as read from tags at the time the record was written (re-match aid).</summary>
    public string Title { get; set; } = "";

    /// <summary>Track artist as read from tags at the time the record was written (re-match aid).</summary>
    public string Artist { get; set; } = "";

    /// <summary>
    /// Base lyric offset for the whole track in milliseconds, or <b>null</b>
    /// when this track has no per-track value and should inherit the global
    /// <c>AppSettings.LyricsOffsetMs</c>. Positive = lyrics appear later (same
    /// sign convention as the global setting). Sections override it per span.
    /// <para>
    /// Nullable on purpose: a record that exists only to pin a lyrics source
    /// must not silently pin the offset to 0 and mask the global slider.
    /// </para>
    /// </summary>
    public double? LyricsOffsetMs { get; set; }

    /// <summary>Pinned provider id ("lrc", "lrclib", "spotify-color", ...); empty = default chain.</summary>
    public string LyricsSourceId { get; set; } = "";

    /// <summary>
    /// Pinned search title, or <b>null</b> to use the file tag. Set together with
    /// <see cref="LyricsArtist"/> when the operator binds lyrics for a cover: every
    /// provider is keyed on artist+title, so the file's tags are the wrong query.
    /// </summary>
    public string? LyricsTitle { get; set; }

    /// <summary>Pinned search artist, or <b>null</b> to use the file tag. See <see cref="LyricsTitle"/>.</summary>
    public string? LyricsArtist { get; set; }

    /// <summary>
    /// Optional per-track follow mode ("Line"/"Word"); null = use the global
    /// setting. Stored as the enum name so a future mode does not corrupt the file.
    /// </summary>
    public string? LyricsFollowMode { get; set; }

    /// <summary>
    /// Offset sections, always sorted, non-overlapping and half-open
    /// (<c>[StartMs, EndMs)</c>) - the store normalises on write and on load.
    /// </summary>
    public List<LyricOffsetSection> Sections { get; set; } = new();

    /// <summary>
    /// Operator line-text edits, ordered by <see cref="LyricLineEdit.TimeMs"/>.
    /// Applied after the provider chain resolves a document, matched to the line
    /// whose start time is nearest within ±250 ms, and only when the document's
    /// source id equals the edit's (see <see cref="LyricEdits.Apply"/>).
    /// <para>
    /// Never empty-but-null: an older file without the array loads as an empty
    /// list, so a missing array and "no edits" behave the same.
    /// </para>
    /// </summary>
    public List<LyricLineEdit> LyricEdits { get; set; } = new();

    /// <summary>When the record was last written (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Deep copy, used for the save snapshot and for handing out read-only state.</summary>
    public TrackMetadata Clone() => new()
    {
        Key = Key,
        Title = Title,
        Artist = Artist,
        LyricsOffsetMs = LyricsOffsetMs,
        LyricsSourceId = LyricsSourceId,
        LyricsTitle = LyricsTitle,
        LyricsArtist = LyricsArtist,
        LyricsFollowMode = LyricsFollowMode,
        Sections = new List<LyricOffsetSection>(Sections),
        LyricEdits = new List<LyricLineEdit>(LyricEdits),
        UpdatedUtc = UpdatedUtc,
    };
}

/// <summary>
/// Result of <see cref="TrackMetadataStore.AddSection"/>.
/// <para>
/// <see cref="Added"/> is false when the span was rejected: empty/inverted, or
/// overlapping a section that starts earlier (earlier span wins - see
/// <see cref="LyricOffsetTimeline.Normalize"/>). <see cref="ReplacedSections"/>
/// counts later spans the new section displaced, so the UI can tell the user
/// "this replaced the end of the chorus".
/// </para>
/// </summary>
public readonly record struct SectionAddResult(
    bool Added,
    int Index,
    int ReplacedSections,
    string? RejectReason)
{
    public static SectionAddResult Rejected(string reason) => new(false, -1, 0, reason);
}

/// <summary>
/// Persistent per-track lyric metadata, stored as one indented JSON document at
/// <c>%APPDATA%\JockeyBox\track-metadata.json</c> and indexed in memory by
/// <see cref="TrackMetadata.Key"/> for O(1) lookup.
///
/// <para><b>Why a JSON document and not SQLite:</b> the whole library is a few
/// thousand tracks of a few hundred bytes each - the file stays well under a
/// megabyte, loads in a few milliseconds and needs no native dependency. The
/// in-memory dictionary is the read path; the file is only a write-through log.</para>
///
/// <para><b>Threading:</b> every public member takes one private lock, so reads
/// from the UI thread are safe while the debounced save runs on the thread
/// pool. <see cref="Changed"/> is raised <i>after</i> the lock is released, so a
/// listener may call straight back into the store.</para>
///
/// <para><b>Failure policy:</b> no public member throws for I/O or JSON
/// problems. A failed save logs and keeps the in-memory state (the next debounce
/// retries); a corrupt file logs, falls back to the last good backup, and then
/// to an empty document.</para>
///
/// <para><b>Saving:</b> changes mark the document dirty and arm a one-second
/// timer. The timer is re-armed on each change, so dragging the offset slider
/// produces at most one write per second (usually one, when the drag ends).
/// <see cref="Save"/> flushes immediately for shutdown/exit.</para>
/// </summary>
public sealed class TrackMetadataStore : IDisposable
{
    /// <summary>Document schema version written to and expected in the file.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Offset bound, in milliseconds. Mirrors
    /// <c>AppSettings.MaxLyricsOffsetMs</c>; written here so the store has no
    /// dependency on the settings class.
    /// </summary>
    public const double MaxOffsetMs = 10_000;

    private const int SaveDebounceMs = 1000;
    private const string FileName = "track-metadata.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Audio extensions stripped by <see cref="NormalizeKey"/>. Deliberately a
    /// short allow-list: a key like "Artist/Vol.2 Song" must keep its ".2".
    /// </summary>
    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".wav", ".ogg", ".oga", ".opus",
        ".wma", ".aiff", ".aif", ".alac", ".m4b", ".webm", ".mka", ".ape", ".wv",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, TrackMetadata> _tracks = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly Timer _saveTimer;

    // Identity index ("artist\u001Ftitle" -> key, title -> keys), rebuilt lazily.
    private Dictionary<string, string>? _byIdentity;
    private Dictionary<string, List<string>>? _byTitle;

    private bool _dirty;
    private bool _legacyImported;
    private bool _disposed;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public TrackMetadataStore(string? filePath = null)
    {
        _path = string.IsNullOrWhiteSpace(filePath) ? DefaultPath : filePath!;
        _saveTimer = new Timer(OnSaveTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    // ---- construction / paths ---------------------------------------------

    /// <summary><c>%APPDATA%\JockeyBox\track-metadata.json</c>.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.Home, FileName);

    /// <summary>File this store reads and writes.</summary>
    public string FilePath => _path;

    /// <summary>Number of tracks in memory.</summary>
    public int Count
    {
        get { lock (_gate) return _tracks.Count; }
    }

    /// <summary>
    /// Construct a store at <paramref name="filePath"/> (default
    /// <see cref="DefaultPath"/>) and load it. The app cannot fail to start
    /// because of this: a missing or corrupt file yields an empty store.
    /// </summary>
    public static TrackMetadataStore Open(string? filePath = null)
    {
        var store = new TrackMetadataStore(filePath);
        store.Load();
        return store;
    }

    // ---- lookup ------------------------------------------------------------

    /// <summary>
    /// Normalise a key for storage/lookup: path separators folded to <c>/</c>,
    /// duplicate and leading/trailing separators removed, a known audio
    /// extension stripped, then lowercased (invariant). Null/blank yields "".
    /// <para>
    /// A <c>TrackEntry.RelativeKey</c> is already extension-stripped, so this is
    /// idempotent for it; the extension strip is only there for callers that
    /// pass a file name or path.
    /// </para>
    /// </summary>
    public static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";

        var text = key.Trim().Replace('\\', '/');

        // Strip the leading "./" that relative paths sometimes carry.
        while (text.StartsWith("./", StringComparison.Ordinal)) text = text[2..];

        var extension = Path.GetExtension(text);
        if (extension.Length > 1 && AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            text = text[..^extension.Length];

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts).ToLowerInvariant();
    }

    /// <summary>
    /// Record for <paramref name="key"/>, or null. The instance is the live
    /// object: read it, do not mutate it (use the setters).
    /// </summary>
    public TrackMetadata? Get(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return null;

        lock (_gate)
        {
            return _tracks.TryGetValue(normalized, out var record) ? record : null;
        }
    }

    /// <summary>True when a record exists for the key.</summary>
    public bool Contains(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return false;
        lock (_gate) return _tracks.ContainsKey(normalized);
    }

    /// <summary>All normalised keys, sorted; a snapshot for the settings/export UI.</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate)
            {
                var keys = _tracks.Keys.ToList();
                keys.Sort(StringComparer.Ordinal);
                return keys;
            }
        }
    }

    /// <summary>
    /// Create the record when missing (never overwrites an existing one), mark
    /// the document dirty and return the live record. A blank key returns a
    /// detached record that is <b>not</b> stored - callers should pass a real
    /// <c>RelativeKey</c>.
    /// </summary>
    public TrackMetadata GetOrCreate(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return new TrackMetadata();

        bool created;
        TrackMetadata record;
        lock (_gate)
        {
            if (!_tracks.TryGetValue(normalized, out var existing))
            {
                existing = new TrackMetadata { Key = normalized, UpdatedUtc = DateTime.UtcNow };
                _tracks[normalized] = existing;
                _dirty = true;
                created = true;
            }
            else
            {
                created = false;
            }
            record = existing;
        }

        if (created)
        {
            ScheduleSave();
            RaiseChanged(normalized, TrackMetadataChange.Created);
        }
        return record;
    }

    /// <summary>Underlying record without creating one; same as <see cref="Get"/>.</summary>
    public TrackMetadata? TryGet(string? key) => Get(key);

    // ---- base fields -------------------------------------------------------

    /// <summary>
    /// Save the track's base offset (clamped to +/-<see cref="MaxOffsetMs"/>).
    /// This makes the value per-track: it no longer follows the global slider.
    /// Use <see cref="ClearLyricsOffset"/> to go back to inheriting it.
    /// </summary>
    public void SetLyricsOffset(string? key, double ms)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return;

        var value = ClampOffset(ms);
        lock (_gate)
        {
            var record = EnsureLocked(normalized);
            if (record.LyricsOffsetMs is { } existing && Math.Abs(existing - value) < 0.0001) return;
            record.LyricsOffsetMs = value;
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Property);
    }

    /// <summary>
    /// Drop the per-track offset so the track inherits the global value again
    /// (the "Reset" button on a TRACK scope). True when a value was removed;
    /// sections are untouched.
    /// </summary>
    public bool ClearLyricsOffset(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return false;

        lock (_gate)
        {
            if (!_tracks.TryGetValue(normalized, out var record) || record.LyricsOffsetMs is null) return false;
            record.LyricsOffsetMs = null;
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Property);
        return true;
    }

    /// <summary>
    /// The track's own base offset, or <paramref name="globalOffsetMs"/> when it
    /// has none (never set, or cleared). This is the value the slider shows.
    /// </summary>
    public double GetBaseOffset(string? key, double globalOffsetMs)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return globalOffsetMs;

        lock (_gate)
        {
            return _tracks.TryGetValue(normalized, out var record) && record.LyricsOffsetMs is { } value
                ? value
                : globalOffsetMs;
        }
    }

    /// <summary>True when the track has its own offset (as opposed to inheriting the global one).</summary>
    public bool HasOwnOffset(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return false;

        lock (_gate)
        {
            return _tracks.TryGetValue(normalized, out var record) && record.LyricsOffsetMs is not null;
        }
    }

    /// <summary>Save the pin for a provider; null/blank clears it (back to the default chain).</summary>
    public void SetLyricsSource(string? key, string? sourceId)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return;

        var value = string.IsNullOrWhiteSpace(sourceId) ? "" : sourceId.Trim();
        lock (_gate)
        {
            var record = EnsureLocked(normalized);
            if (string.Equals(record.LyricsSourceId, value, StringComparison.Ordinal)) return;
            record.LyricsSourceId = value;
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Property);
    }

    /// <summary>
    /// Save the pinned search query. Blank/null on both clears it back to the file
    /// tags, which is also what the panel's "use file tags" control does.
    /// </summary>
    public void SetLyricsQuery(string? key, string? title, string? artist)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return;

        var newTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        var newArtist = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
        lock (_gate)
        {
            var record = EnsureLocked(normalized);
            if (string.Equals(record.LyricsTitle, newTitle, StringComparison.Ordinal)
                && string.Equals(record.LyricsArtist, newArtist, StringComparison.Ordinal))
            {
                return;
            }

            record.LyricsTitle = newTitle;
            record.LyricsArtist = newArtist;
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Property);
    }

    /// <summary>
    /// Save the per-track follow mode ("Line"/"Word"); null clears it and falls
    /// back to the global setting.
    /// </summary>
    public void SetLyricsFollowMode(string? key, string? mode)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return;

        var value = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
        lock (_gate)
        {
            var record = EnsureLocked(normalized);
            if (string.Equals(record.LyricsFollowMode, value, StringComparison.OrdinalIgnoreCase)) return;
            record.LyricsFollowMode = value;
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Property);
    }

    // ---- operator line edits ------------------------------------------------

    /// <summary>
    /// Replace the operator's line-text edits for a track; null/empty clears them,
    /// which puts the provider's own text back. Stored sorted by time so the caller
    /// gets a stable list, and compared by value so re-saving the same edits touches
    /// neither the file nor the <see cref="Changed"/> listeners.
    /// <para>
    /// Nothing is pruned here: an edit that does not match the current document
    /// (another source, or no line in range) is kept for when that source returns.
    /// </para>
    /// </summary>
    public void SetLyricEdits(string? key, IEnumerable<LyricLineEdit>? edits)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return;

        var next = NormalizeEdits(edits);

        lock (_gate)
        {
            var record = EnsureLocked(normalized);
            if (SameEdits(record.LyricEdits, next)) return;
            record.LyricEdits = next;
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Property);
    }

    /// <summary>Operator line edits for a track, sorted by time; empty when none. A copy.</summary>
    public IReadOnlyList<LyricLineEdit> GetLyricEdits(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return Array.Empty<LyricLineEdit>();

        lock (_gate)
        {
            return _tracks.TryGetValue(normalized, out var record) && record.LyricEdits.Count > 0
                ? record.LyricEdits.ToArray()
                : Array.Empty<LyricLineEdit>();
        }
    }

    /// <summary>
    /// Missing, null or empty input becomes an empty list - which is how an older
    /// <c>track-metadata.json</c> without the array keeps loading. Blank entries are
    /// dropped; the rest are trimmed and ordered by time.
    /// </summary>
    private static List<LyricLineEdit> NormalizeEdits(IEnumerable<LyricLineEdit>? raw)
    {
        if (raw is null) return new List<LyricLineEdit>();

        var list = new List<LyricLineEdit>();
        foreach (var edit in raw)
        {
            if (edit is null) continue;
            list.Add(edit with
            {
                TimeMs = Math.Max(0, edit.TimeMs),
                Text = edit.Text ?? "",
                SourceId = edit.SourceId?.Trim() ?? "",
            });
        }
        list.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return list;
    }

    private static bool SameEdits(List<LyricLineEdit> a, List<LyricLineEdit> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Text, b[i].Text, StringComparison.Ordinal)
                || a[i].TimeMs != b[i].TimeMs
                || !string.Equals(a[i].SourceId, b[i].SourceId, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// Record the track's title/artist so a moved file can be re-matched later
    /// (<see cref="TryResolveByMetadata"/>). Blank values leave the stored ones.
    /// </summary>
    public void SetTrackInfo(string? key, string? title, string? artist)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return;

        var newTitle = string.IsNullOrWhiteSpace(title) ? "" : title.Trim();
        var newArtist = string.IsNullOrWhiteSpace(artist) ? "" : artist.Trim();

        lock (_gate)
        {
            var record = EnsureLocked(normalized);
            var changed = false;
            if (newTitle.Length > 0 && !string.Equals(record.Title, newTitle, StringComparison.Ordinal))
            {
                record.Title = newTitle;
                changed = true;
            }
            if (newArtist.Length > 0 && !string.Equals(record.Artist, newArtist, StringComparison.Ordinal))
            {
                record.Artist = newArtist;
                changed = true;
            }
            if (!changed) return;

            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
            _byIdentity = null;
            _byTitle = null;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Identity);
    }

    // ---- sections ----------------------------------------------------------

    /// <summary>
    /// Sections for a track, sorted and half-open; empty when none. A copy, so
    /// the caller can hold it while a save runs.
    /// </summary>
    public IReadOnlyList<LyricOffsetSection> GetSections(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return Array.Empty<LyricOffsetSection>();

        lock (_gate)
        {
            return _tracks.TryGetValue(normalized, out var record)
                ? record.Sections.ToArray()
                : Array.Empty<LyricOffsetSection>();
        }
    }

    /// <summary>
    /// Add a section spanning <c>[startMs, endMs)</c> that uses
    /// <paramref name="offsetMs"/> instead of the track base offset (override
    /// semantics - see <see cref="LyricOffsetTimeline"/>).
    /// <para>
    /// Overlap policy: <b>the earlier span wins</b>. A new span that begins
    /// before an existing one displaces it (<see cref="SectionAddResult.ReplacedSections"/>);
    /// a new span that begins inside an existing one is rejected with
    /// <see cref="SectionAddResult.Added"/> false, so a user's carefully placed
    /// section is never silently merged. Times are clamped to >= 0 and the span
    /// must be at least one millisecond long.
    /// </para>
    /// </summary>
    public SectionAddResult AddSection(
        string? key,
        int startMs,
        int endMs,
        int offsetMs,
        string? label = null)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return SectionAddResult.Rejected("no track key");

        var section = new LyricOffsetSection(startMs, endMs, ClampOffsetInt(offsetMs), label).Clamped();
        if (!section.IsValid) return SectionAddResult.Rejected("empty span (end must be after start)");

        SectionAddResult result;
        lock (_gate)
        {
            var record = EnsureLocked(normalized);
            foreach (var existing in record.Sections)
            {
                if (existing.Overlaps(section) && existing.StartMs <= section.StartMs)
                {
                    return SectionAddResult.Rejected(
                        $"overlaps the section at {existing.StartMs}-{existing.EndMs} ms; " +
                        "trim or remove that section first");
                }
            }

            var combined = new List<LyricOffsetSection>(record.Sections.Count + 1);
            combined.AddRange(record.Sections);
            combined.Add(section);

            var (kept, dropped) = LyricOffsetTimeline.NormalizeWithReport(combined);
            record.Sections = new List<LyricOffsetSection>(kept);

            var index = -1;
            for (int i = 0; i < kept.Count; i++)
            {
                if (kept[i].StartMs == section.StartMs && kept[i].EndMs == section.EndMs
                    && kept[i].OffsetMs == section.OffsetMs)
                {
                    index = i;
                    break;
                }
            }

            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
            result = index >= 0
                ? new SectionAddResult(true, index, dropped, null)
                : SectionAddResult.Rejected("displaced by an earlier section");

            if (dropped > 0)
                Log.Warn($"track-metadata: {normalized}: {dropped} section(s) dropped as overlapping");
        }

        ScheduleSave();
        if (result.Added) RaiseChanged(normalized, TrackMetadataChange.Sections);
        return result;
    }

    /// <summary>Remove the section at <paramref name="index"/>; true when it existed.</summary>
    public bool RemoveSection(string? key, int index)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return false;

        lock (_gate)
        {
            if (!_tracks.TryGetValue(normalized, out var record)) return false;
            if (index < 0 || index >= record.Sections.Count) return false;

            record.Sections.RemoveAt(index);
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Sections);
        return true;
    }

    /// <summary>
    /// Replace one section's offset, keeping its span. The "adjust just this
    /// part" path: the editor moves the section by a delta and writes the
    /// absolute result here. True when the index existed.
    /// </summary>
    public bool SetSectionOffset(string? key, int index, int offsetMs)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return false;

        var value = ClampOffsetInt(offsetMs);
        lock (_gate)
        {
            if (!_tracks.TryGetValue(normalized, out var record)) return false;
            if (index < 0 || index >= record.Sections.Count) return false;

            var current = record.Sections[index];
            if (current.OffsetMs == value) return false;

            record.Sections[index] = current with { OffsetMs = value };
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Sections);
        return true;
    }

    /// <summary>
    /// Replace one section's span and offset (dragging its edges / re-selecting
    /// lines). Overlapping neighbours are re-normalised with the same
    /// earlier-wins policy; returns false when the section or span is invalid.
    /// </summary>
    public bool UpdateSection(string? key, int index, int startMs, int endMs, int offsetMs, string? label = null)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return false;

        var updated = new LyricOffsetSection(startMs, endMs, ClampOffsetInt(offsetMs), label).Clamped();
        if (!updated.IsValid) return false;

        lock (_gate)
        {
            if (!_tracks.TryGetValue(normalized, out var record)) return false;
            if (index < 0 || index >= record.Sections.Count) return false;

            var combined = new List<LyricOffsetSection>(record.Sections);
            combined[index] = updated;

            var (kept, dropped) = LyricOffsetTimeline.NormalizeWithReport(combined);
            record.Sections = new List<LyricOffsetSection>(kept);
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;

            if (dropped > 0)
                Log.Warn($"track-metadata: {normalized}: {dropped} section(s) dropped updating section {index}");
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Sections);
        return true;
    }

    /// <summary>Remove every section for a track; returns how many were removed.</summary>
    public int ClearSections(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return 0;

        int removed;
        lock (_gate)
        {
            if (!_tracks.TryGetValue(normalized, out var record)) return 0;
            removed = record.Sections.Count;
            if (removed == 0) return 0;

            record.Sections.Clear();
            record.UpdatedUtc = DateTime.UtcNow;
            _dirty = true;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Sections);
        return removed;
    }

    /// <summary>
    /// The offset the highlighter must use at <paramref name="positionMs"/>:
    /// the track's base offset (or <paramref name="globalOffsetMs"/> when the
    /// track has none), or - inside a section - that section's offset
    /// <b>instead of</b> the base (override semantics; see
    /// <see cref="LyricOffsetTimeline.OffsetAt"/> and
    /// <see cref="GetEffectiveOffsetCumulative"/> for the additive variant).
    /// <para>
    /// Positive = lyrics later, matching the rest of the lyric code. A position
    /// exactly at a section's end is outside it; negative positions clamp to 0.
    /// </para>
    /// <para>
    /// Pass <c>settings.LyricsOffsetMs</c> as <paramref name="globalOffsetMs"/>
    /// so a track with no saved value keeps following the global slider. Sections
    /// apply even when the track has no base of its own.
    /// </para>
    /// </summary>
    public double GetEffectiveOffset(string? key, int positionMs, double globalOffsetMs = 0)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return globalOffsetMs;

        lock (_gate)
        {
            if (!_tracks.TryGetValue(normalized, out var record)) return globalOffsetMs;
            var baseOffset = record.LyricsOffsetMs ?? globalOffsetMs;
            return LyricOffsetTimeline.OffsetAt(
                (int)Math.Round(baseOffset, MidpointRounding.AwayFromZero),
                record.Sections,
                positionMs);
        }
    }

    /// <inheritdoc cref="GetEffectiveOffset(string, int, double)"/>
    public double GetEffectiveOffset(string? key, TimeSpan position, double globalOffsetMs = 0) =>
        GetEffectiveOffset(key, LyricOffsetTimeline.ToPositionMs(position), globalOffsetMs);

    /// <summary>
    /// Additive variant: base + section delta inside a section. Only for tools
    /// that treat a section as a relative nudge; the highlighter uses
    /// <see cref="GetEffectiveOffset(string, int, double)"/>.
    /// </summary>
    public double GetEffectiveOffsetCumulative(string? key, int positionMs, double globalOffsetMs = 0)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return globalOffsetMs;

        lock (_gate)
        {
            if (!_tracks.TryGetValue(normalized, out var record)) return globalOffsetMs;
            var baseOffset = record.LyricsOffsetMs ?? globalOffsetMs;
            return LyricOffsetTimeline.OffsetAtCumulative(
                (int)Math.Round(baseOffset, MidpointRounding.AwayFromZero),
                record.Sections,
                positionMs);
        }
    }

    /// <summary>Index of the section containing the position, or -1 (for the section editor's highlight).</summary>
    public int SectionIndexAt(string? key, int positionMs)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return -1;

        lock (_gate)
        {
            return _tracks.TryGetValue(normalized, out var record)
                ? LyricOffsetTimeline.SectionIndexAt(record.Sections, positionMs)
                : -1;
        }
    }

    // ---- re-matching a moved file -----------------------------------------

    /// <summary>
    /// Find a key by tag identity after the file moved (and so got a new
    /// <c>RelativeKey</c>). Exact normalised artist+title wins; otherwise a
    /// unique title match is used (artist tags differ between files of the same
    /// song all the time). Ambiguous titles return null rather than guessing.
    /// </summary>
    public string? TryResolveByMetadata(string? title, string? artist)
    {
        var titleKey = NormalizeIdentityPart(title);
        if (titleKey.Length == 0) return null;
        var artistKey = NormalizeIdentityPart(artist);
        var identity = artistKey + "\u001F" + titleKey;

        lock (_gate)
        {
            EnsureIdentityIndexLocked();

            if (_byIdentity!.TryGetValue(identity, out var key)) return key;

            if (_byTitle!.TryGetValue(titleKey, out var keys) && keys.Count == 1) return keys[0];
            return null;
        }
    }

    // ---- removal -----------------------------------------------------------

    /// <summary>Drop a track's record entirely; true when it existed.</summary>
    public bool Remove(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return false;

        lock (_gate)
        {
            if (!_tracks.Remove(normalized)) return false;
            _dirty = true;
            _byIdentity = null;
            _byTitle = null;
        }

        ScheduleSave();
        RaiseChanged(normalized, TrackMetadataChange.Removed);
        return true;
    }

    // ---- legacy migration --------------------------------------------------

    /// <summary>
    /// True when the one-time import of <c>AppSettings</c>'s per-track lyric
    /// dictionaries has run (persisted, so a user who clears an offset keeps it
    /// cleared).
    /// </summary>
    public bool LegacyImportDone
    {
        get { lock (_gate) return _legacyImported; }
    }

    /// <summary>
    /// Fence the one-time legacy import without importing anything (used when
    /// settings hold no per-track lyric data, so the pass does not repeat on
    /// every launch).
    /// </summary>
    public void MarkLegacyImportDone()
    {
        lock (_gate)
        {
            if (_legacyImported) return;
            _legacyImported = true;
            _dirty = true;
        }
        ScheduleSave();
    }

    /// <summary>
    /// Import legacy per-track values (<c>AppSettings.LyricsOffsetByTrack</c> /
    /// <c>LyricsSourceByTrack</c>, both keyed by <c>RelativeKey</c>) into this
    /// store. Read-only towards the caller's dictionaries, idempotent (guarded
    /// by <see cref="LegacyImportDone"/>), and it never overwrites a value the
    /// store already holds for that key. <see cref="Metadata.TrackMetadataMigration"/>
    /// wraps this for <c>AppSettings</c>.
    /// </summary>
    /// <returns>
    /// How many values were created or filled in - a track that gains both an
    /// offset and a source contributes two, so this is not a track count.
    /// </returns>
    public int ImportLegacy(
        IReadOnlyDictionary<string, double>? offsetsByTrack,
        IReadOnlyDictionary<string, string>? sourcesByTrack)
    {
        int imported = 0;

        lock (_gate)
        {
            if (_legacyImported) return 0;

            if (offsetsByTrack is not null)
            {
                foreach (var pair in offsetsByTrack)
                {
                    var normalized = NormalizeKey(pair.Key);
                    if (normalized.Length == 0) continue;
                    if (!_tracks.TryGetValue(normalized, out var record))
                    {
                        record = new TrackMetadata { Key = normalized, UpdatedUtc = DateTime.UtcNow };
                        _tracks[normalized] = record;
                        imported++;
                    }
                    // Never overwrite a store value: the store is newer than
                    // settings.json by definition, since settings predate it.
                    if (record.LyricsOffsetMs is null)
                    {
                        record.LyricsOffsetMs = ClampOffset(pair.Value);
                        imported++;
                    }
                }
            }

            if (sourcesByTrack is not null)
            {
                foreach (var pair in sourcesByTrack)
                {
                    var normalized = NormalizeKey(pair.Key);
                    if (normalized.Length == 0 || string.IsNullOrWhiteSpace(pair.Value)) continue;
                    if (!_tracks.TryGetValue(normalized, out var record))
                    {
                        record = new TrackMetadata { Key = normalized, UpdatedUtc = DateTime.UtcNow };
                        _tracks[normalized] = record;
                        imported++;
                    }
                    if (record.LyricsSourceId.Length == 0)
                    {
                        record.LyricsSourceId = pair.Value.Trim();
                        imported++;
                    }
                }
            }

            _legacyImported = true;
            _dirty = true;
        }

        if (imported > 0)
        {
            Log.Info($"track-metadata: imported {imported} legacy lyric setting(s)");
            ScheduleSave();
        }

        return imported;
    }

    // ---- load / save -------------------------------------------------------

    /// <summary>
    /// Read the document into memory. Missing file = empty store (not an
    /// error). Corrupt file = log, fall back to the <c>.bak</c> written by the
    /// last successful replace, then to empty. Never throws.
    /// </summary>
    /// <returns>Number of track records loaded.</returns>
    public int Load()
    {
        TrackMetadataDocument? document = null;
        var primary = _path;
        var backup = _path + ".bak";

        document = TryReadDocument(primary, out var primaryFailed);
        if (document is null && primaryFailed && File.Exists(backup))
        {
            Log.Warn($"track-metadata: {primary} unreadable, trying backup");
            document = TryReadDocument(backup, out _);
            if (document is not null)
                Log.Info("track-metadata: recovered from backup");
        }

        if (document is null) return 0;

        var loaded = 0;
        lock (_gate)
        {
            _tracks.Clear();
            _byIdentity = null;
            _byTitle = null;

            foreach (var raw in document.Tracks ?? new List<TrackMetadata>())
            {
                var normalized = NormalizeKey(raw.Key);
                if (normalized.Length == 0) continue;

                var record = new TrackMetadata
                {
                    Key = normalized,
                    Title = raw.Title?.Trim() ?? "",
                    Artist = raw.Artist?.Trim() ?? "",
                    LyricsOffsetMs = raw.LyricsOffsetMs is { } rawOffset ? ClampOffset(rawOffset) : null,
                    LyricsSourceId = string.IsNullOrWhiteSpace(raw.LyricsSourceId) ? "" : raw.LyricsSourceId.Trim(),
                    LyricsTitle = string.IsNullOrWhiteSpace(raw.LyricsTitle) ? null : raw.LyricsTitle.Trim(),
                    LyricsArtist = string.IsNullOrWhiteSpace(raw.LyricsArtist) ? null : raw.LyricsArtist.Trim(),
                    LyricsFollowMode = string.IsNullOrWhiteSpace(raw.LyricsFollowMode) ? null : raw.LyricsFollowMode.Trim(),
                    // A missing array (older file) deserialises to null here and
                    // becomes an empty list, so those files keep loading.
                    LyricEdits = NormalizeEdits(raw.LyricEdits),
                    Sections = new List<LyricOffsetSection>(),
                    UpdatedUtc = raw.UpdatedUtc,
                };

                var (sections, dropped) = LyricOffsetTimeline.NormalizeWithReport(raw.Sections);
                record.Sections.AddRange(sections);
                if (dropped > 0)
                    Log.Warn($"track-metadata: {normalized}: {dropped} invalid/overlapping section(s) dropped on load");

                _tracks[normalized] = record; // later duplicate wins; keys are normalised
                loaded++;
            }

            _legacyImported = document.LegacyLyricsImported;
            _dirty = false;
        }

        Log.Info($"track-metadata: loaded {loaded} track record(s) from {_path}");
        return loaded;
    }

    /// <summary>
    /// Write the document now (temp file + atomic replace, previous file kept as
    /// <c>.bak</c>). Safe to call from a shutdown handler; never throws.
    /// </summary>
    /// <returns>True when the write succeeded.</returns>
    public bool Save()
    {
        List<TrackMetadata> snapshot;
        bool legacyImported;

        lock (_gate)
        {
            if (_disposed && !_dirty) return false;
            snapshot = _tracks.Values.Select(t => t.Clone()).ToList();
            legacyImported = _legacyImported;
            _dirty = false;
        }

        var document = new TrackMetadataDocument
        {
            Version = SchemaVersion,
            LegacyLyricsImported = legacyImported,
            Tracks = snapshot,
        };

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(document, JsonOptions);
            var temp = _path + ".tmp";
            var backup = _path + ".bak";

            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(temp, _path, backup, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    File.Move(temp, _path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, _path, overwrite: true);
            }

            lock (_gate) _lastSaveUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"track-metadata: save failed for {_path}", ex);
            lock (_gate) _dirty = true; // a later debounce retries
            return false;
        }
        finally
        {
            try
            {
                var temp = _path + ".tmp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>UTC time of the last successful write, or <see cref="DateTime.MinValue"/>.</summary>
    public DateTime LastSaveUtc
    {
        get { lock (_gate) return _lastSaveUtc; }
    }

    // ---- events ------------------------------------------------------------

    /// <summary>
    /// Raised after any mutation, outside the lock. Not raised by
    /// <see cref="Load"/> or by <see cref="ImportLegacy"/>'s bulk pass (the
    /// listener wires up after that).
    /// </summary>
    public event EventHandler<TrackMetadataChangedEventArgs>? Changed;

    private void RaiseChanged(string key, TrackMetadataChange change)
    {
        var handler = Changed;
        if (handler is null) return;
        try { handler(this, new TrackMetadataChangedEventArgs(key, change)); }
        catch (Exception ex) { Log.Error("track-metadata: Changed handler threw", ex); }
    }

    // ---- internals ---------------------------------------------------------

    private TrackMetadata EnsureLocked(string normalizedKey)
    {
        if (_tracks.TryGetValue(normalizedKey, out var record)) return record;

        record = new TrackMetadata { Key = normalizedKey, UpdatedUtc = DateTime.UtcNow };
        _tracks[normalizedKey] = record;
        _dirty = true;
        return record;
    }

    private void ScheduleSave()
    {
        if (_disposed) return;
        try
        {
            _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Disposal raced a change; the final Save() on Dispose already ran.
        }
    }

    private void OnSaveTimer(object? state)
    {
        try
        {
            lock (_gate)
            {
                if (!_dirty || _disposed) return;
            }
            Save();
        }
        catch (Exception ex)
        {
            Log.Error("track-metadata: debounced save failed", ex);
        }
    }

    private void EnsureIdentityIndexLocked()
    {
        if (_byIdentity is not null && _byTitle is not null) return;

        _byIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
        _byTitle = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var record in _tracks.Values)
        {
            var titleKey = NormalizeIdentityPart(record.Title);
            if (titleKey.Length == 0) continue;

            var identity = NormalizeIdentityPart(record.Artist) + "\u001F" + titleKey;
            if (!_byIdentity.ContainsKey(identity)) _byIdentity[identity] = record.Key;

            if (!_byTitle.TryGetValue(titleKey, out var keys))
            {
                keys = new List<string>();
                _byTitle[titleKey] = keys;
            }
            keys.Add(record.Key);
        }
    }

    private static string NormalizeIdentityPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            builder.Append(ch);
            lastWasSpace = false;
        }
        return builder.ToString();
    }

    private TrackMetadataDocument? TryReadDocument(string path, out bool failed)
    {
        failed = false;
        try
        {
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var document = JsonSerializer.Deserialize<TrackMetadataDocument>(json, JsonOptions);
            if (document is null)
            {
                failed = true;
                Log.Warn($"track-metadata: {path} deserialised to null");
                return null;
            }

            if (document.Version > SchemaVersion)
                Log.Warn($"track-metadata: {path} is version {document.Version} (this build understands {SchemaVersion}); reading known fields only");

            return document;
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Error($"track-metadata: cannot read {path}", ex);
            return null;
        }
    }

    private static double ClampOffset(double ms)
    {
        if (double.IsNaN(ms)) return 0;
        return Math.Clamp(ms, -MaxOffsetMs, MaxOffsetMs);
    }

    private static int ClampOffsetInt(int ms) =>
        (int)Math.Clamp(ms, -(int)MaxOffsetMs, (int)MaxOffsetMs);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Dispose();
        }
        catch { /* best-effort */ }

        // Flush whatever the debounce still owed.
        try
        {
            bool pending;
            lock (_gate) pending = _dirty;
            if (pending) Save();
        }
        catch (Exception ex)
        {
            Log.Error("track-metadata: final save failed", ex);
        }
    }

    /// <summary>
    /// On-disk shape: <c>{"Version":1,"Tracks":[...]}</c>. Public so the harness
    /// and an export tool can round-trip the exact document.
    /// </summary>
    public sealed class TrackMetadataDocument
    {
        public int Version { get; set; } = SchemaVersion;
        public bool LegacyLyricsImported { get; set; }
        public List<TrackMetadata> Tracks { get; set; } = new();
    }
}

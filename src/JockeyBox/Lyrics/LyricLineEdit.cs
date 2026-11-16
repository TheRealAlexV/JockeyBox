using System;
using System.Collections.Generic;

namespace JockeyBox.Lyrics;

/// <summary>
/// One operator edit of a single lyric line: the line is named by its start time
/// (in milliseconds) and carries the provider id it was authored against.
/// <para>
/// The source id is part of the identity on purpose. A timestamp from LRCLIB and
/// a timestamp from the Spotify color provider are different lines of the same
/// song, so an edit must not follow the text onto another provider's timing: it
/// simply stops matching and stays in the store until that source comes back.
/// </para>
/// </summary>
public sealed record LyricLineEdit
{
    /// <summary>Start time of the edited line in milliseconds; the matching key.</summary>
    public int TimeMs { get; init; }

    /// <summary>Replacement text for the whole line.</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// <see cref="LyricsDocument.SourceId"/> of the document this edit was authored
    /// against. Empty only for a document restored from a pre-source-id cache file.
    /// </summary>
    public string SourceId { get; init; } = "";
}

/// <summary>
/// Applies <see cref="LyricLineEdit"/>s to a resolved document. Line-level text
/// only: the line's <see cref="LyricLine.Time"/>, the document's line times and
/// the sections built from them never move, and the document's own
/// <see cref="LyricsDocument.SourceId"/> is left alone so precedence and the
/// lyrics cache key are unaffected.
/// <para>
/// This runs <b>after</b> the provider chain and only on the in-memory document.
/// The lyrics cache is written before this point (see
/// <see cref="LyricsService"/>), which is what keeps edited text off the
/// <c>artist|title|seconds|sourceId</c> cache entry: two files with the same tags
/// and duration share that entry, so edited text there would leak onto both.
/// </para>
/// </summary>
public static class LyricEdits
{
    /// <summary>
    /// How far a line's start time may sit from the edit's and still bind. Wide
    /// enough to survive a provider re-rounding a timestamp, narrow enough that a
    /// different line of a sung song is never in reach.
    /// </summary>
    public const int MatchWindowMs = 250;

    /// <summary>
    /// <see cref="LyricsDocument.SourceId"/> for a document built purely from
    /// operator edits, when no provider produced one (see <see cref="FromEdits"/>).
    /// </summary>
    public const string EditSourceId = "edit";

    /// <summary>Human-readable <see cref="LyricsDocument.Source"/> for that document.</summary>
    public const string EditSource = "Operator edits";

    /// <summary>
    /// Index of the line this edit binds to within ±<see cref="MatchWindowMs"/>,
    /// nearest wins, or -1 when nothing is close enough.
    /// </summary>
    public static int FindLine(IReadOnlyList<LyricLine>? lines, LyricLineEdit edit)
    {
        if (lines is null || lines.Count == 0 || edit is null) return -1;

        int best = -1, bestDelta = MatchWindowMs + 1;
        for (int i = 0; i < lines.Count; i++)
        {
            int delta = (int)Math.Abs(lines[i].Time.TotalMilliseconds - edit.TimeMs);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Return <paramref name="doc"/> with every matching edit applied, or the same
    /// instance when nothing matched. Edits whose source id differs from the
    /// document's, and edits with no line in range, are skipped and stay in the
    /// store: they are not pruned here, and they render nothing.
    /// </summary>
    /// <param name="trackDuration">
    /// Used only to bound the last line's window, exactly as
    /// <see cref="WordTimeline.Synthesize"/> does.
    /// </param>
    public static LyricsDocument? Apply(
        LyricsDocument? doc, IReadOnlyList<LyricLineEdit>? edits, TimeSpan? trackDuration = null)
    {
        if (doc?.Lines is null or { Count: 0 } || edits is null || edits.Count == 0) return doc;

        List<LyricLine>? lines = null;
        for (int e = 0; e < edits.Count; e++)
        {
            var edit = edits[e];
            if (edit is null) continue;
            // Source guard: the edit is meaningless against another provider's timing.
            if (!string.Equals(doc.SourceId, edit.SourceId, StringComparison.Ordinal)) continue;

            var current = lines ?? (IReadOnlyList<LyricLine>)doc.Lines;
            int index = FindLine(current, edit);
            if (index < 0) continue;

            lines ??= new List<LyricLine>(doc.Lines);
            var line = lines[index];
            // The window is the line's own, unchanged: both bounds come from the
            // document's line times, never from the replacement text's length.
            var (start, end) = WordTimeline.LineWindow(lines, index, trackDuration);
            var words = WordTimeline.BuildLineWords(edit.Text, start, end);

            lines[index] = line with
            {
                Text = edit.Text,
                Words = words.Count > 0 ? words : null,
                IsSynthetic = true,
            };
        }

        if (lines is null) return doc;
        return new LyricsDocument
        {
            Synced = doc.Synced,
            Source = doc.Source,
            SourceId = doc.SourceId,
            Lines = lines,
        };
    }

    /// <summary>
    /// The edits as an unsynced document, for a track no provider answered for:
    /// the operator still gets their lyric sheet, just without a highlight. Null
    /// when there is nothing to show.
    /// <para>
    /// <see cref="LyricsDocument.SourceId"/> is the single source the edits were
    /// authored against, or <see cref="EditSourceId"/> when they disagree - the
    /// document is no one provider's, and saying so keeps the pinned-source
    /// header honest.
    /// </para>
    /// </summary>
    public static LyricsDocument? FromEdits(IReadOnlyList<LyricLineEdit>? edits)
    {
        if (edits is null || edits.Count == 0) return null;

        var lines = new List<LyricLine>(edits.Count);
        string? source = null;
        bool mixed = false;

        // Ordered by time so the sheet reads like the song; the store already
        // sorts, this keeps the method total for any caller.
        var ordered = new List<LyricLineEdit>(edits);
        ordered.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

        for (int i = 0; i < ordered.Count; i++)
        {
            var edit = ordered[i];
            if (edit is null) continue;
            if (source is null) source = edit.SourceId;
            else if (!string.Equals(source, edit.SourceId, StringComparison.Ordinal)) mixed = true;

            lines.Add(new LyricLine(TimeSpan.FromMilliseconds(edit.TimeMs), edit.Text) { IsSynthetic = true });
        }

        if (lines.Count == 0) return null;

        return new LyricsDocument
        {
            Synced = false,
            Source = EditSource,
            SourceId = mixed || string.IsNullOrEmpty(source) ? EditSourceId : source!,
            Lines = lines,
        };
    }
}

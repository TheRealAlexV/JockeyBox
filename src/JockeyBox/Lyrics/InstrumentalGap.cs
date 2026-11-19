using System;
using System.Collections.Generic;

namespace JockeyBox.Lyrics;

/// <summary>
/// Insert a display-only marker line into the instrumental stretches of a synced
/// document, so the highlight stops sitting on the last sung line for the whole
/// chorus and the words of that line stop stretching across the gap.
/// <para>
/// Call this <b>before</b> <see cref="WordTimeline.Synthesize"/>: the marker
/// becomes the pre-gap line's next line, which is what bounds its word window.
/// </para>
/// <para>
/// Never mutates its input. A document with nothing to insert comes back as the
/// same instance, and a document that already carries a marker comes back
/// unchanged, so calling this twice cannot double-insert.
/// </para>
/// </summary>
public static class InstrumentalGap
{
    /// <summary>
    /// A gap at least this long counts as instrumental. A heuristic standing in
    /// for a vocal-end measurement the lyric data does not contain: no provider
    /// says when singing stops, only when the next line starts, so a slow song
    /// with a long held note is indistinguishable from a gap this short.
    /// </summary>
    public const int GapThresholdMs = 10_000;

    /// <summary>
    /// How long after its own start a line may stay highlighted before the marker
    /// takes over. A heuristic standing in for the same missing vocal-end
    /// measurement: it also stops a long instrumental tail being credited to the
    /// last word of the line before it.
    /// </summary>
    public const int SungCapMs = 5_000;

    /// <summary>Text of the marker line: music symbols, no words to time.</summary>
    public const string Marker = "\u266a\u266b\u2669\u266c\u266d";

    private static readonly TimeSpan SungCap = TimeSpan.FromMilliseconds(SungCapMs);

    /// <summary>
    /// <paramref name="doc"/> with a marker line in every instrumental stretch, or
    /// the same instance when there is nothing to insert (unsynced document, no
    /// long gap, or markers already present).
    /// </summary>
    public static LyricsDocument Fill(LyricsDocument doc)
    {
        if (doc is null || !doc.Synced) return doc!;

        var lines = doc.Lines;
        if (lines is null || lines.Count == 0) return doc;
        if (HasMarker(lines)) return doc;

        var filled = new List<LyricLine>(lines.Count + 1);

        // Instrumental intro: nothing has been sung yet, so the marker opens the
        // document and holds the highlight until the first line.
        if (lines[0].Time.TotalMilliseconds >= GapThresholdMs)
            filled.Add(MarkerLine(TimeSpan.Zero));

        for (int i = 0; i < lines.Count; i++)
        {
            filled.Add(lines[i]);
            if (i + 1 >= lines.Count) continue;

            var gap = lines[i + 1].Time - lines[i].Time;
            if (gap.TotalMilliseconds >= GapThresholdMs)
                filled.Add(MarkerLine(lines[i].Time + SungCap));
        }

        if (filled.Count == lines.Count) return doc;

        // A new document, never a mutation: the provider's copy stays the one the
        // lyrics cache and the operator's edits were keyed against.
        return new LyricsDocument
        {
            Synced = doc.Synced,
            Source = doc.Source,
            SourceId = doc.SourceId,
            Lines = filled,
        };
    }

    private static LyricLine MarkerLine(TimeSpan time) => new(time, Marker) { IsInstrumental = true };

    private static bool HasMarker(List<LyricLine> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].IsInstrumental) return true;
        }
        return false;
    }
}

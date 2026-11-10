using JockeyBox.Audio.Beat;

namespace JockeyBox.Library;

/// <summary>
/// The one rule for what tempo a library row shows. Deliberately the same shape as
/// <see cref="TrackKeyResolver"/>.
///
/// <para><b>Tag first.</b> An authored TBPM tag is the file's own assertion about the
/// recording; the analysed tempo is an estimator's best guess (and one that can land
/// on the half- or double-time octave). Letting the estimate overwrite a stated tempo
/// would be a silent downgrade, so a tag always wins and analysis only fills a blank.</para>
///
/// <para>0 means "no tempo known", matching <see cref="TrackEntry.Bpm"/> and the row's
/// em-dash fallback. Callers use this both at scan time (tag or cached grid) and when
/// analysis finishes for an already-visible row.</para>
/// </summary>
public static class TrackBpmResolver
{
    public static double Resolve(double tagBpm, BeatGrid? grid)
    {
        if (tagBpm > 0) return tagBpm;
        return grid is { Bpm: > 0 } analysed ? analysed.Bpm : 0;
    }
}

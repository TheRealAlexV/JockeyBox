namespace JockeyBox.Dsp;

/// <summary>
/// Ratio → group-delay curve for the tempo stretcher, in frames at a reference
/// rate (48 kHz).
///
/// <para><b>Why a table.</b> SoundTouch exposes no buffered-sample count and its
/// sequence/seek windows make the delay depend on the ratio, so the value cannot
/// be derived from the API — only measured. <see cref="Measured"/> holds a real
/// measurement (method in its comment); <see cref="Default"/> is empty.</para>
///
/// <para><b>Empty means unknown, not zero.</b> <see cref="FramesFor"/> returns 0
/// for an empty table, which is the honest "we do not know" answer: the caller
/// then loses the compensation but never invents one. A guessed compensation is
/// worse than a known zero, because it turns a visible lead into an invisible
/// lag.</para>
/// </summary>
public sealed class TempoLatencyTable
{
    private readonly (double Ratio, int FramesAt48k)[] _points;

    public TempoLatencyTable((double Ratio, int FramesAt48k)[] points) => _points = points;

    /// <summary>Empty until calibrated: no compensation, never a wrong one.</summary>
    public static TempoLatencyTable Default { get; } = new(Array.Empty<(double, int)>());

    /// <summary>
    /// Measured on this repo's SoundTouch.Net 2.3.2 by running the real
    /// <c>TempoStage</c> over a probe of 24 × 30 ms bursts spread across 12 s of
    /// 48 kHz stereo, at each ratio, and fitting the time map
    /// <c>out·ratio = slope·media + intercept</c> by least squares over the
    /// burst energy centroids. slope came out 1.0000 ± 0.001 at every ratio,
    /// which validates the tempo semantics; the intercept is the media-domain
    /// group delay. Worst per-burst residual was 10 ms, so entries are rounded
    /// to the frame and the curve is only trusted to ~5 ms.
    ///
    /// <para>Spacing is 0.1 across 0.5–2.0 with 0.95/1.05 bracketing the unity
    /// bypass. A 0.05 sweep showed the curve's own jitter (±1.5 ms) exceeds the
    /// linear-interpolation error at this spacing (≤ ~2.5 ms), so a denser table
    /// would encode measurement noise rather than signal.</para>
    ///
    /// <para>Measured intercepts below ratio 0.75 came out marginally negative
    /// (−3 to −8 ms) despite a causal stretcher being unable to lead its input;
    /// that is WSOLA repeat-segment bias in the centroid, so those points are
    /// clamped to 0 rather than applied as a forward jump of the playhead.</para>
    /// </summary>
    public static TempoLatencyTable Measured { get; } = new(new (double, int)[]
    {
        (0.50, 0), (0.60, 0), (0.70, 41), (0.80, 190), (0.90, 456), (0.95, 604),
        (1.05, 832), (1.10, 915), (1.20, 1081), (1.30, 1082), (1.40, 1294),
        (1.50, 1340), (1.60, 1385), (1.70, 1682), (1.80, 1723), (1.90, 1653),
        (2.00, 1647),
    });

    /// <summary>True once a real measurement has been loaded.</summary>
    public bool IsCalibrated => _points.Length > 0;

    /// <summary>Interpolated group delay in frames for the given ratio and rate.</summary>
    public int FramesFor(double ratio, int sampleRate)
    {
        if (_points.Length == 0) return 0;

        double at48;
        if (ratio <= _points[0].Ratio) at48 = _points[0].FramesAt48k;
        else if (ratio >= _points[^1].Ratio) at48 = _points[^1].FramesAt48k;
        else
        {
            at48 = _points[^1].FramesAt48k;
            for (int i = 1; i < _points.Length; i++)
            {
                if (ratio > _points[i].Ratio) continue;
                var (r0, f0) = _points[i - 1];
                var (r1, f1) = _points[i];
                double t = (ratio - r0) / Math.Max(1e-9, r1 - r0);
                at48 = f0 + (f1 - f0) * t;
                break;
            }
        }

        return (int)Math.Round(at48 * sampleRate / 48000.0);
    }

    /// <summary>Build a table from measured (ratio, frames) samples sorted by ratio.</summary>
    public static TempoLatencyTable FromMeasurements(IEnumerable<(double Ratio, int Frames)> measured)
    {
        var list = new List<(double, int)>(measured);
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new TempoLatencyTable(list.ToArray());
    }
}

using NAudio.Wave;

namespace JockeyBox.Audio;

/// <summary>
/// Emits a fixed number of silent samples before passing through the source.
/// Used to realize a positive deck delay when the master transport position
/// is still inside the delay window (masterPos < delay).
/// Sample-accurate: silence length is computed in samples, not wall-clock.
/// </summary>
public class SilencePrependSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private long _silenceRemaining; // in samples (across all channels)

    public SilencePrependSampleProvider(ISampleProvider source, TimeSpan silenceDuration)
    {
        _source = source;
        // samples = seconds * sampleRate * channels
        _silenceRemaining = (long)(silenceDuration.TotalSeconds
                                   * source.WaveFormat.SampleRate) * source.WaveFormat.Channels;
        if (_silenceRemaining < 0) _silenceRemaining = 0;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;

        // Phase 1: silence
        if (_silenceRemaining > 0)
        {
            int silent = (int)Math.Min(_silenceRemaining, count);
            Array.Clear(buffer, offset, silent);
            _silenceRemaining -= silent;
            written += silent;
        }

        // Phase 2: real audio
        if (written < count)
            written += _source.Read(buffer, offset + written, count - written);

        return written;
    }
}

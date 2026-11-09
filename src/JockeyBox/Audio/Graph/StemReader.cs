using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// One audio file, already normalised to the engine's format.
///
/// <para>The 2.0 graph has a single sample rate and channel count, because
/// <c>MixingSampleProvider</c> is a summer, not a converter: feeding it a 44.1 kHz
/// mono stem and a 48 kHz stereo stem plays one at the wrong speed and corrupts
/// the frame layout of the other. Every stem is therefore converted here — once,
/// on load — and nothing downstream ever has to care what the file was.</para>
///
/// <para><see cref="Read"/> never returns zero or short: at the end of the file
/// it emits silence. A 0-length read means "input ended" to NAudio's mixer and
/// would remove the deck from the graph in the middle of a fade.</para>
///
/// <para><b>Sync offset.</b> The karaoke stem carries a user-tunable offset
/// (<c>DeckChannel.Karaoke.DelayMs</c>, ±5 s): a positive value delays it by
/// prepending silence, a negative value trims the head of the karaoke file. Both
/// are expressed in frames here, so the offset is sample-accurate regardless of
/// tempo.</para>
/// </summary>
public sealed class StemReader : ISampleProvider, IDisposable
{
    private readonly MediaFoundationReader _reader;
    private readonly ISampleProvider _normalised;
    private readonly int _channels;
    private readonly int _rate;

    private long _silenceFrames;
    private long _skipFrames;
    private readonly float[] _scratch;

    private StemReader(MediaFoundationReader reader, ISampleProvider normalised, int rate, int channels, string path)
    {
        _reader = reader;
        _normalised = normalised;
        _rate = rate;
        _channels = channels;
        Path = path;
        _scratch = new float[channels * 4096];
    }

    /// <summary>Open a file and normalise it to <paramref name="engineRate"/> / <paramref name="engineChannels"/>.</summary>
    public static StemReader Open(string path, int engineRate, int engineChannels)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("audio file not found", path);

        var reader = new MediaFoundationReader(path);
        ISampleProvider sp = reader.ToSampleProvider();

        if (sp.WaveFormat.Channels == 1 && engineChannels >= 2)
            sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > engineChannels)
            sp = new TakeChannelsSampleProvider(sp, engineChannels);

        if (sp.WaveFormat.SampleRate != engineRate)
            sp = new WdlResamplingSampleProvider(sp, engineRate);

        return new StemReader(reader, sp, engineRate, engineChannels, path);
    }

    public string Path { get; }

    public WaveFormat WaveFormat => _normalised.WaveFormat;

    /// <summary>File duration in seconds.</summary>
    public double DurationSeconds
    {
        get
        {
            try { return _reader.TotalTime.TotalSeconds; }
            catch { return 0; }
        }
    }

    /// <summary>Source position, for diagnostics only — the deck owns the playhead.</summary>
    public TimeSpan SourcePosition
    {
        get
        {
            try { return _reader.CurrentTime; }
            catch { return TimeSpan.Zero; }
        }
    }

    /// <summary>
    /// Set the sync offset. Positive = delay this stem (prepend silence),
    /// negative = trim its head. Call before playback starts, or from a render
    /// command, because it re-seeks the file.
    /// </summary>
    public void SetSyncOffset(double milliseconds)
    {
        _silenceFrames = 0;
        _skipFrames = 0;

        if (Math.Abs(milliseconds) < 0.01) return;

        long frames = (long)Math.Round(Math.Abs(milliseconds) * _rate / 1000.0);
        if (milliseconds > 0)
        {
            _silenceFrames = frames;
        }
        else
        {
            _skipFrames = frames;
        }
    }

    /// <summary>Seek the file (render thread only).</summary>
    public void Seek(TimeSpan position)
    {
        _silenceFrames = 0;
        _skipFrames = 0;
        try
        {
            var total = _reader.TotalTime;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > total) position = total;
            _reader.CurrentTime = position;
        }
        catch (Exception ex)
        {
            JockeyBox.Tools.Log.Warn($"stem seek failed for {System.IO.Path.GetFileName(Path)}: {ex.Message}");
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _channels;
        int produced = 0;

        // 1. Head trim (negative sync offset).
        while (_skipFrames > 0 && produced < frames)
        {
            int take = (int)Math.Min(_skipFrames, frames - produced);
            Samples.ReadFull(_normalised, _scratch, 0, take * _channels);
            _skipFrames -= take;
            produced += take;
            if (_skipFrames == 0) break;
        }

        // 2. Prepend silence (positive sync offset).
        while (_silenceFrames > 0 && produced < frames)
        {
            int take = (int)Math.Min(_silenceFrames, frames - produced);
            Array.Clear(buffer, offset + produced * _channels, take * _channels);
            _silenceFrames -= take;
            produced += take;
        }

        // 3. Real audio, padded with silence at the end of the file.
        if (produced < frames)
        {
            int want = (frames - produced) * _channels;
            Samples.ReadFull(_normalised, buffer, offset + produced * _channels, want);
            produced = frames;
        }

        return produced * _channels;
    }

    public void Dispose()
    {
        try { _reader.Dispose(); } catch { }
    }
}

/// <summary>Keeps the first <c>n</c> channels of a multi-channel source (5.1 file into a stereo engine).</summary>
internal sealed class TakeChannelsSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _in;
    private readonly int _out;
    private float[] _scratch = Array.Empty<float>();

    public TakeChannelsSampleProvider(ISampleProvider source, int outChannels)
    {
        _source = source;
        _in = source.WaveFormat.Channels;
        _out = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _out;
        int need = frames * _in;
        if (_scratch.Length < need) _scratch = new float[need];
        int got = _source.Read(_scratch, 0, need);
        int gotFrames = got / _in;
        for (int f = 0; f < gotFrames; f++)
            for (int c = 0; c < _out && c < _in; c++)
                buffer[offset + f * _out + c] = _scratch[f * _in + c];
        if (gotFrames < frames)
            Array.Clear(buffer, offset + gotFrames * _out, (frames - gotFrames) * _out);
        return count;
    }
}

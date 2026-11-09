using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NAudio.Wave;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Graph;

/// <summary>
/// One soundboard sound: a short file that plays on top of the mix, retriggerable,
/// optionally looping.
///
/// <para><b>Trigger latency.</b> A pad must sound when it is hit, so the file is
/// opened and converted up front (on load), the trigger is queued as a render
/// command, and playback starts at the top of the next 10 ms block. Nothing is
/// read from disk on trigger.</para>
///
/// <para><b>Voice stealing.</b> Pressing a pad again while it is still ringing
/// restarts it from the top rather than layering a second copy — layering is how a
/// soundboard turns into a wall of noise during a show. Overlapping copies are
/// only produced by explicitly enabling <see cref="AllowOverlap"/>.</para>
/// </summary>
public sealed class SoundboardVoice : IDisposable
{
    private readonly int _rate;
    private readonly int _channels;
    private readonly float[] _scratch;
    private readonly RenderCommands _commands = new();

    private StemReader? _reader;
    private string? _path;
    private volatile bool _playing;
    private volatile bool _loop;
    private float _gain = 1f;
    private float _smoothGain = 1f;

    public SoundboardVoice(int sampleRate, int channels)
    {
        _rate = sampleRate;
        _channels = channels;
        _scratch = new float[AudioEngine.BlockFrames * channels];
    }

    /// <summary>Stable id (the bank slot) used by the UI and MIDI bindings.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>File currently loaded, if any.</summary>
    public string? Path => _path;

    /// <summary>True while audio is being produced.</summary>
    public bool IsPlaying => _playing;

    /// <summary>Loop until stopped (ambience beds, applause).</summary>
    public bool Loop
    {
        get => _loop;
        set => _loop = value;
    }

    /// <summary>Voice gain (0..2).</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0f, 2f);
    }

    /// <summary>True when a file is loaded and ready to trigger.</summary>
    public bool IsLoaded => _reader != null;

    /// <summary>Load (or replace) the file. Safe to call while playing: the swap is deferred to the render thread.</summary>
    public async Task LoadAsync(string path)
    {
        StemReader reader;
        try
        {
            reader = await Task.Run(() => StemReader.Open(path, _rate, _channels)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"soundboard load failed: {System.IO.Path.GetFileName(path)}", ex);
            throw;
        }

        _commands.Post(() =>
        {
            _reader?.Dispose();
            _reader = reader;
            _path = path;
            _playing = false;
            _smoothGain = _gain;
        });
    }

    /// <summary>Trigger from the top (restart if already playing).</summary>
    public void Play() => _commands.Post(() =>
    {
        if (_reader == null) return;
        _reader.Seek(TimeSpan.Zero);
        _playing = true;
    });

    /// <summary>Stop and rewind.</summary>
    public void Stop() => _commands.Post(() =>
    {
        _playing = false;
        _reader?.Seek(TimeSpan.Zero);
    });

    public void Unload() => _commands.Post(() =>
    {
        _playing = false;
        _reader?.Dispose();
        _reader = null;
        _path = null;
    });

    /// <summary>Render this voice into <paramref name="dst"/> (additive), render thread only.</summary>
    public void Render(float[] dst, int offset, int frames)
    {
        _commands.Drain();
        if (!_playing || _reader == null) return;

        int samples = frames * _channels;
        if (_scratch.Length < samples) return;

        Samples.ReadFull(_reader, _scratch, 0, samples);

        // ~3 ms gain ramp so retriggers and fader moves do not click.
        float step = 1f / Math.Max(1f, 0.003f * _rate);
        for (int f = 0; f < frames; f++)
        {
            if (_smoothGain < _gain) _smoothGain = Math.Min(_gain, _smoothGain + step);
            else if (_smoothGain > _gain) _smoothGain = Math.Max(_gain, _smoothGain - step);

            int baseIdx = offset + f * _channels;
            int src = f * _channels;
            for (int c = 0; c < _channels; c++) dst[baseIdx + c] += _scratch[src + c] * _smoothGain;
        }

        // End detection: a non-looping voice stops when the file has been consumed.
        if (!_loop && _reader.DurationSeconds > 0)
        {
            double elapsed = _reader.SourcePosition.TotalSeconds;
            if (elapsed >= _reader.DurationSeconds - 0.02)
            {
                _playing = false;
                _reader.Seek(TimeSpan.Zero);
            }
        }
    }

    public void Dispose()
    {
        _playing = false;
        try { _reader?.Dispose(); } catch { }
        _reader = null;
    }
}

/// <summary>
/// The soundboard voice pool. Rendered into the same block as the decks and added
/// before the limiter, so a pad is part of the program the audience (and OBS)
/// hears, not a side channel.
/// </summary>
public sealed class SoundboardBus : IDisposable
{
    private readonly int _channels;
    private readonly List<SoundboardVoice> _voices = new();
    private readonly object _gate = new();

    public SoundboardBus(int sampleRate, int channels)
    {
        _channels = channels;
        SampleRate = sampleRate;
        _busScratch = new float[AudioEngine.BlockFrames * channels];
    }

    public int SampleRate { get; }

    public IReadOnlyList<SoundboardVoice> Voices
    {
        get { lock (_gate) return _voices.ToArray(); }
    }

    /// <summary>Add or replace a voice by id.</summary>
    public SoundboardVoice GetOrAdd(string id, int channels)
    {
        lock (_gate)
        {
            var existing = _voices.Find(v => v.Id == id);
            if (existing != null) return existing;
            var voice = new SoundboardVoice(SampleRate, channels) { Id = id };
            _voices.Add(voice);
            return voice;
        }
    }

    private readonly float[] _busScratch;

    /// <summary>Render every active voice into the block (additive, scaled by the bus gain).</summary>
    public void Render(float[] dst, int offset, int frames, float busGain = 1f)
    {
        SoundboardVoice[] snapshot;
        lock (_gate) snapshot = _voices.ToArray();
        if (snapshot.Length == 0) return;

        int samples = frames * _channels;
        if (_busScratch.Length < samples) return;

        foreach (var voice in snapshot)
        {
            try
            {
                Array.Clear(_busScratch, 0, samples);
                // Render unconditionally: a voice's Play/Stop commands are drained
                // inside Render, so skipping "not playing" voices here would never
                // process the first Play after a cold load.
                voice.Render(_busScratch, 0, frames);
                Samples.AddScaled(dst, _busScratch, offset, samples, busGain);
            }
            catch (Exception ex) { Log.Error("soundboard voice failed", ex); }
        }
    }

    /// <summary>Stop everything (panic button / track change).</summary>
    public void StopAll()
    {
        SoundboardVoice[] snapshot;
        lock (_gate) snapshot = _voices.ToArray();
        foreach (var voice in snapshot) voice.Stop();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var voice in _voices) voice.Dispose();
            _voices.Clear();
        }
    }
}

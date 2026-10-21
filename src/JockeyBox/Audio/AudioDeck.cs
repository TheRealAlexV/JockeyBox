using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JockeyBox.Audio;

/// <summary>
/// One playback deck: a loaded audio file, an output device selection, a
/// millisecond delay offset and a volume. The deck does not own the clock -
/// SyncTransport primes and starts both decks against a shared master position.
///
/// Delay model (sample-accurate, survives seek/pause):
///   deck read position = master position - DelayMs
///   * if negative, the gap is filled with prepended silence
///   * positive DelayMs  => this deck is heard LATER  than the master clock
///   * negative DelayMs  => this deck is heard EARLIER than the master clock
/// </summary>
public class AudioDeck : IDisposable
{
    private readonly DeviceService _devices;

    private MediaFoundationReader? _reader;   // decodes mp3/m4a/wav/flac/wma via Media Foundation
    private WasapiOut? _output;               // one independent WASAPI session per deck
    private MMDevice? _device;                // owned COM object; disposed with the output
    private VolumeSampleProvider? _volumeProvider;

    private float _volume = 1.0f;

    public AudioDeck(string name, DeviceService devices)
    {
        Name = name;
        _devices = devices;
    }

    public string Name { get; }

    /// <summary>WASAPI endpoint ID this deck routes to (null = system default).</summary>
    public string? DeviceId { get; set; }

    /// <summary>Playback offset in milliseconds relative to the master clock.</summary>
    public double DelayMs { get; set; }

    /// <summary>WASAPI shared-mode buffer latency per deck, in ms.</summary>
    public int OutputLatencyMs { get; set; } = 80;

    public string? FilePath { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsLoaded => _reader != null;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1.5f);
            // Volume changes apply live, without re-priming the pipeline.
            if (_volumeProvider != null) _volumeProvider.Volume = _volume;
        }
    }

    /// <summary>Load an audio file into the deck (stops any current playback).</summary>
    public void Load(string filePath)
    {
        StopAndDisposeOutput();
        _reader?.Dispose();
        _reader = null; // never leave a disposed reader dangling if the ctor throws
        FilePath = null;
        Duration = TimeSpan.Zero;

        _reader = new MediaFoundationReader(filePath);
        FilePath = filePath;
        Duration = _reader.TotalTime;
    }

    public void Unload()
    {
        StopAndDisposeOutput();
        _reader?.Dispose();
        _reader = null;
        FilePath = null;
        Duration = TimeSpan.Zero;
    }

    /// <summary>
    /// Build the playback pipeline for the given master position and fill the
    /// WASAPI buffer. After this call the deck is armed; Start() begins render.
    /// </summary>
    public void Prime(TimeSpan masterPosition)
    {
        if (_reader == null) return;
        StopAndDisposeOutput();

        var delay = TimeSpan.FromMilliseconds(DelayMs);
        var readerPos = masterPosition - delay;

        TimeSpan silence = TimeSpan.Zero;
        if (readerPos < TimeSpan.Zero)
        {
            silence = -readerPos;      // still inside the delay window
            readerPos = TimeSpan.Zero;
        }
        if (readerPos > _reader.TotalTime) readerPos = _reader.TotalTime;

        _reader.CurrentTime = readerPos;

        ISampleProvider chain = _reader.ToSampleProvider();
        if (silence > TimeSpan.Zero)
            chain = new SilencePrependSampleProvider(chain, silence);

        _volumeProvider = new VolumeSampleProvider(chain) { Volume = _volume };

        _device = _devices.ResolveDevice(DeviceId);
        try
        {
            _output = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, OutputLatencyMs);
            _output.Init(_volumeProvider); // sets up the WASAPI client; render starts on Play()
        }
        catch
        {
            StopAndDisposeOutput(); // don't leak a half-built pipeline on failure
            throw;
        }
    }

    /// <summary>Begin rendering. Call immediately after Prime on both decks.</summary>
    public void Start() => _output?.Play();

    public void StopAndDisposeOutput()
    {
        if (_output != null)
        {
            try { _output.Stop(); } catch { /* device may have vanished */ }
            _output.Dispose();
            _output = null;
        }
        _device?.Dispose(); // WasapiOut does not own the MMDevice - we do
        _device = null;
        _volumeProvider = null;
    }

    public void Dispose()
    {
        StopAndDisposeOutput();
        _reader?.Dispose();
        _reader = null;
    }
}

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JockeyBox.Audio;

/// <summary>
/// One audio stem: a loaded file, an output device selection and a millisecond
/// delay offset. Unlike the old shared-clock design, every deck owns its own
/// playhead (base position + stopwatch), so two tracks can run independently
/// and be mixed with the crossfader in <see cref="DualDeckTransport"/>.
///
/// A deck's karaoke version is a second <see cref="AudioDeck"/> owned by
/// <see cref="DeckChannel"/>, primed at the primary stem's position so the two
/// stay sample-accurate.
///
/// Delay model (sample-accurate, survives seek/pause):
///   stem read position = deck position - DelayMs
///   * if negative, the gap is filled with prepended silence
///   * positive DelayMs  => this stem is heard LATER  than the deck playhead
///   * negative DelayMs  => this stem is heard EARLIER than the deck playhead
///
/// The primary stem of a channel always runs with DelayMs = 0; the delay only
/// offsets the karaoke sub-stem so its peaks can be aligned against the original.
/// </summary>
public class AudioDeck : IDisposable
{
    private readonly DeviceService _devices;
    private readonly Stopwatch _clock = new();

    private MediaFoundationReader? _reader;   // decodes mp3/m4a/wav/flac/wma via Media Foundation
    private WasapiOut? _output;               // one independent WASAPI session per stem
    private MMDevice? _device;                // owned COM object; disposed with the output
    private VolumeSampleProvider? _volumeProvider;

    private TimeSpan _basePosition = TimeSpan.Zero;
    private float _volume = 1.0f;

    public AudioDeck(string name, DeviceService devices)
    {
        Name = name;
        _devices = devices;
    }

    public string Name { get; }

    /// <summary>WASAPI endpoint ID this stem routes to (null = system default).</summary>
    public string? DeviceId { get; set; }

    /// <summary>Playback offset in milliseconds relative to the deck playhead.</summary>
    public double DelayMs { get; set; }

    /// <summary>WASAPI shared-mode buffer latency per stem, in ms.</summary>
    public int OutputLatencyMs { get; set; } = 80;

    public string? FilePath { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsLoaded => _reader != null;

    /// <summary>This stem's own transport state.</summary>
    public TransportState State { get; private set; } = TransportState.Stopped;

    /// <summary>Raised after <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Unclamped playhead: base position plus elapsed time while playing. The
    /// owning channel uses this to detect the end of the media.
    /// </summary>
    public TimeSpan Elapsed
    {
        get
        {
            var pos = State == TransportState.Playing
                ? _basePosition + _clock.Elapsed
                : _basePosition;
            return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
        }
    }

    /// <summary>Playhead clamped to the loaded media (readout / seek bar).</summary>
    public TimeSpan Position => Elapsed > Duration ? Duration : Elapsed;

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

    /// <summary>Load an audio file into the stem (stops any current playback).</summary>
    public void Load(string filePath)
    {
        StopAndDisposeOutput();
        _reader?.Dispose();
        _reader = null; // never leave a disposed reader dangling if the ctor throws
        FilePath = null;
        Duration = TimeSpan.Zero;
        _basePosition = TimeSpan.Zero;
        _clock.Reset();

        _reader = new MediaFoundationReader(filePath);
        FilePath = filePath;
        Duration = _reader.TotalTime;
        SetState(TransportState.Stopped);
    }

    public void Unload()
    {
        StopAndDisposeOutput();
        _reader?.Dispose();
        _reader = null;
        FilePath = null;
        Duration = TimeSpan.Zero;
        _basePosition = TimeSpan.Zero;
        _clock.Reset();
        SetState(TransportState.Stopped);
    }

    /// <summary>
    /// Build the playback pipeline for the given deck position and fill the
    /// WASAPI buffer. After this call the stem is armed; <see cref="BeginRender"/>
    /// begins render. Used by both the standalone transport helpers below and by
    /// <see cref="DeckChannel"/> when it starts a deck and its karaoke sub-stem
    /// back-to-back.
    /// </summary>
    public void ArmAt(TimeSpan position)
    {
        if (_reader == null) return;

        position = ClampToMedia(position);
        _basePosition = position;
        _clock.Reset();

        StopAndDisposeOutput();

        var delay = TimeSpan.FromMilliseconds(DelayMs);
        var readerPos = position - delay;

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

        // Armed, not started: the deck's playhead starts when BeginRender runs.
        SetState(TransportState.Paused);
    }

    /// <summary>
    /// Begin rendering and start this stem's playhead. Call immediately after
    /// arming the primary stem and its karaoke partner, so the two Start() calls
    /// execute back-to-back.
    /// </summary>
    public void BeginRender()
    {
        if (_reader == null) return;
        try { _output?.Play(); }
        catch { StopAndDisposeOutput(); } // a vanished device must not crash the mix
        _clock.Restart();
        SetState(TransportState.Playing);
    }

    /// <summary>
    /// Freeze the playhead and drop the output pipeline. The position is kept so
    /// the next <see cref="Play"/> resumes where this one paused.
    /// </summary>
    public void Pause()
    {
        if (State != TransportState.Playing) return;
        _basePosition = Position;
        _clock.Reset();
        StopAndDisposeOutput();
        SetState(TransportState.Paused);
    }

    /// <summary>Stop and rewind to zero.</summary>
    public void Stop()
    {
        _clock.Reset();
        _basePosition = TimeSpan.Zero;
        StopAndDisposeOutput();
        SetState(TransportState.Stopped);
    }

    /// <summary>Move the playhead while parked (paused/stopped).</summary>
    public void SetPosition(TimeSpan position)
    {
        _basePosition = ClampToMedia(position);
        _clock.Reset();
    }

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

    private TimeSpan ClampToMedia(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return TimeSpan.Zero;
        return t > Duration ? Duration : t;
    }

    private void SetState(TransportState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        StopAndDisposeOutput();
        _reader?.Dispose();
        _reader = null;
    }
}

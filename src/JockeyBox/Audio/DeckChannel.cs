using JockeyBox.Playback;

namespace JockeyBox.Audio;

/// <summary>
/// One DJ deck: a primary stem (the track as loaded, normally the original
/// music file) plus, when the track has a karaoke partner, a second
/// <see cref="AudioDeck"/> for the instrumental that sits inside this deck.
///
/// The deck owns its own playhead - the primary stem's clock drives
/// <see cref="Position"/>/<see cref="State"/> - and the karaoke sub-stem is
/// armed at the same position with its own delay offset, so starting or seeking
/// the deck moves both stems sample-accurately.
///
/// <see cref="DeckChannel.KaraokeGain"/> is the karaoke control:
/// 0 = original only, 1 = karaoke only, 0.5 = both.
/// </summary>
public sealed class DeckChannel : IDisposable
{
    public const double MinDelayMs = -5000;
    public const double MaxDelayMs = 5000;

    private readonly DeviceService _devices;

    private double _volume = 1.0;
    private double _karaokeGain = 0.5;
    private double _faderGain = 1.0;
    private double _delayMs;
    private string? _deviceId;

    public DeckChannel(string name, DeviceService devices)
    {
        Name = name;
        _devices = devices;
        Primary = new AudioDeck(name, devices);
        Karaoke = new AudioDeck(name + " karaoke", devices);
        ApplyVolumes();
    }

    public string Name { get; }

    /// <summary>The stem the deck's playhead, duration and end detection follow.</summary>
    public AudioDeck Primary { get; }

    /// <summary>The karaoke sub-stem; <see cref="AudioDeck.IsLoaded"/> is false without a partner.</summary>
    public AudioDeck Karaoke { get; }

    public bool IsLoaded => Primary.IsLoaded || Karaoke.IsLoaded;
    public bool HasKaraoke => Karaoke.IsLoaded;

    public string? PrimaryPath => Primary.FilePath;
    public string? KaraokePath => Karaoke.IsLoaded ? Karaoke.FilePath : null;

    /// <summary>WASAPI endpoint both stems route to (null = system default).</summary>
    public string? DeviceId
    {
        get => _deviceId;
        set
        {
            _deviceId = value;
            Primary.DeviceId = value;
            Karaoke.DeviceId = value;
        }
    }

    /// <summary>Karaoke offset in milliseconds relative to the deck playhead.</summary>
    public double DelayMs
    {
        get => _delayMs;
        set
        {
            _delayMs = Math.Clamp(value, MinDelayMs, MaxDelayMs);
            Karaoke.DelayMs = _delayMs;
            Primary.DelayMs = 0; // the primary stem defines the playhead
        }
    }

    /// <summary>Deck fader (0..1.5, the deck's own volume control).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1.5);
            ApplyVolumes();
        }
    }

    /// <summary>
    /// Karaoke controls: 0 = original only, 1 = karaoke only, 0.5 = both.
    /// </summary>
    public double KaraokeGain
    {
        get => _karaokeGain;
        set
        {
            _karaokeGain = Math.Clamp(value, 0, 1);
            ApplyVolumes();
        }
    }

    /// <summary>Equal-power crossfader gain for this side of the mix (0..1).</summary>
    public double FaderGain
    {
        get => _faderGain;
        set
        {
            _faderGain = Math.Clamp(value, 0, 1);
            ApplyVolumes();
        }
    }

    /// <summary>Loaded length of the deck (longest of its stems).</summary>
    public TimeSpan Duration
    {
        get
        {
            var primary = Primary.IsLoaded ? Primary.Duration : TimeSpan.Zero;
            var karaoke = Karaoke.IsLoaded ? Karaoke.Duration : TimeSpan.Zero;
            return primary > karaoke ? primary : karaoke;
        }
    }

    /// <summary>
    /// Position at which the deck is finished: its length, plus the delay tail
    /// when a positively delayed karaoke stem still has audio to render.
    /// </summary>
    public TimeSpan EndPosition => Duration + TimeSpan.FromMilliseconds(
        Karaoke.IsLoaded ? Math.Max(0, _delayMs) : 0);

    /// <summary>Playhead, clamped to the deck's audible length.</summary>
    public TimeSpan Position
    {
        get
        {
            var pos = Playhead.Elapsed;
            return pos > EndPosition ? EndPosition : pos;
        }
    }

    /// <summary>Position shown in the readout/seek bar (never past the media length).</summary>
    public TimeSpan DisplayPosition
    {
        get
        {
            var pos = Position;
            var dur = Duration;
            return pos > dur ? dur : pos;
        }
    }

    public TransportState State => Playhead.State;

    public bool ReachedEnd =>
        State == TransportState.Playing &&
        EndPosition > TimeSpan.Zero &&
        Elapsed >= EndPosition;

    /// <summary>For the owning channel to detect the media ending between UI ticks.</summary>
    internal TimeSpan Elapsed => Playhead.Elapsed;

    /// <summary>Raised whenever the deck's transport state changes.</summary>
    public event EventHandler? StateChanged;

    private AudioDeck Playhead => Primary.IsLoaded ? Primary : Karaoke;

    /// <summary>
    /// Load a whole track onto this deck: the primary stem plus, when present,
    /// the karaoke partner. A broken karaoke file is dropped instead of taking
    /// the primary stem down with it.
    /// </summary>
    public void Load(string? primaryPath, string? karaokePath)
    {
        Primary.Unload();
        Karaoke.Unload();

        if (string.IsNullOrWhiteSpace(primaryPath))
        {
            RaiseStateChanged();
            return;
        }

        Primary.Load(primaryPath);

        if (!string.IsNullOrWhiteSpace(karaokePath) &&
            !string.Equals(karaokePath, primaryPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Karaoke.Load(karaokePath);
                Karaoke.DelayMs = _delayMs;
            }
            catch
            {
                Karaoke.Unload(); // no instrumental, still a playable deck
            }
        }

        ApplyVolumes();
        RaiseStateChanged();
    }

    public void Unload() => Load(null, null);

    /// <summary>Start (or resume) the deck from its current playhead.</summary>
    public void Play()
    {
        if (!IsLoaded) return;

        var position = Elapsed;
        if (EndPosition > TimeSpan.Zero && position >= EndPosition)
            position = TimeSpan.Zero; // pressed play on a finished deck: start over

        StartAt(position);
    }

    public void Pause()
    {
        if (State != TransportState.Playing) return;
        Primary.Pause();
        Karaoke.Pause();
        RaiseStateChanged();
    }

    public void Stop()
    {
        Primary.Stop();
        Karaoke.Stop();
        RaiseStateChanged();
    }

    /// <summary>Seek both stems; while playing this re-primes and restarts them.</summary>
    public void Seek(TimeSpan position)
    {
        if (!IsLoaded) return;

        position = Clamp(position);

        if (State == TransportState.Playing)
        {
            StartAt(position);
            return;
        }

        Primary.SetPosition(position);
        if (Karaoke.IsLoaded) Karaoke.SetPosition(position);
        RaiseStateChanged();
    }

    public void SkipForward(double seconds = 10) =>
        Seek(DisplayPosition + TimeSpan.FromSeconds(seconds));

    public void SkipBack(double seconds = 10) =>
        Seek(DisplayPosition - TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Re-prime this deck after its device/delay changes so the new routing or
    /// offset applies immediately; a parked deck picks it up on the next Play.
    /// </summary>
    public void ApplySettings()
    {
        if (State == TransportState.Playing)
            StartAt(Elapsed);
    }

    private void StartAt(TimeSpan position)
    {
        position = Clamp(position);

        // Arm both stems while stopped, then start them back-to-back. Residual
        // device start jitter is single-digit ms; the karaoke delay exists to
        // trim that constant offset.
        if (Primary.IsLoaded) TryArm(Primary, position);
        var karaokeReady = Karaoke.IsLoaded && TryArm(Karaoke, position);

        if (Primary.IsLoaded)
        {
            // Runs the playhead even when the device failed, so the clock and
            // the UI keep moving and the other deck can be crossfaded in.
            Primary.BeginRender();
            if (karaokeReady) Karaoke.BeginRender();
        }
        else if (Karaoke.IsLoaded)
        {
            Karaoke.BeginRender();
        }

        RaiseStateChanged();
    }

    private static bool TryArm(AudioDeck deck, TimeSpan position)
    {
        try
        {
            deck.ArmAt(position);
            return true;
        }
        catch
        {
            deck.StopAndDisposeOutput();
            return false;
        }
    }

    private TimeSpan Clamp(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return TimeSpan.Zero;
        var end = EndPosition;
        return position > end ? end : position;
    }

    private void ApplyVolumes()
    {
        // The karaoke control crossfades the deck's two stems: 0 leaves the
        // original alone, 1 hands it to the karaoke instrumental, 0.5 plays both
        // at half. The two files are the same performance, so a linear split
        // keeps the summed level sane (equal power would boost the middle).
        var karaoke = Karaoke.IsLoaded ? _karaokeGain : 0;
        var primary = Karaoke.IsLoaded ? 1 - karaoke : 1;

        Primary.Volume = (float)(_volume * primary * _faderGain);
        Karaoke.Volume = (float)(_volume * karaoke * _faderGain);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        Primary.Dispose();
        Karaoke.Dispose();
    }
}

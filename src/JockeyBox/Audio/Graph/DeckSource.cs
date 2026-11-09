using System;
using System.Threading.Tasks;
using NAudio.Wave;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Graph;

/// <summary>Supplies a per-frame gain for a deck (crossfader envelope × volume × duck).</summary>
public interface IDeckGainSource
{
    /// <summary>Gain for the given engine frame. Called once per frame while the deck plays.</summary>
    float GainAt(long engineFrame);
}

/// <summary>
/// One deck in the 2.0 graph: primary stem + optional karaoke stem, summed and
/// then tempo-stretched as a unit.
///
/// <para><b>Order matters and is deliberate:</b> stems are summed <i>before</i>
/// the stretcher (so a karaoke vocal and its backing track can never drift apart
/// — stretching them separately would), the playhead is counted <i>after</i> the
/// stretcher (so it describes audible time), and the karaoke gain/delay stay on
/// the sub-stem (they are a mix control, not a deck volume).</para>
///
/// <para><b>Never returns 0.</b> A paused, empty or ended deck emits full-length
/// silence. Returning 0 makes NAudio believe the input ended and silently removes
/// it — which during a crossfade would cut the outgoing deck dead.</para>
///
/// <para><b>Threading.</b> Every mutation arrives through <see cref="Commands"/>
/// and is applied at the top of the next <see cref="Read"/>, so the render thread
/// is the only thread that touches readers, seeks or the stretcher. The 1.x code
/// got away without this only because each stem had its own <c>WasapiOut</c>
/// whose render thread fenced MediaFoundation; that fence does not exist in a
/// single shared graph.</para>
/// </summary>
public sealed class DeckSource : ISampleProvider, IDisposable
{
    public enum DeckState
    {
        Empty,
        Loading,
        Loaded,
        Playing,
        Paused,
        Ended,
        Failed,
    }

    private readonly int _rate;
    private readonly int _channels;
    private readonly float[] _karaokeScratch;
    private readonly SumProvider _sum;
    private readonly TempoStage _tempo;

    /// <summary>
    /// Split-render stages: one per stem, created on the first karaoke-mode block
    /// and kept afterwards. Only one of them (or the mix stage) reads per block in
    /// any mode, so the split costs nothing while karaoke mode is off.
    /// </summary>
    private TempoStage? _splitKaraoke;
    private TempoStage? _splitOriginal;
    private bool _splitActive;
    private volatile bool _splitRequested;
    private double _tempoRequested = 1.0;

    private StemReader? _primary;
    private StemReader? _karaoke;

    private DeckState _state = DeckState.Empty;
    private double _durationSeconds;
    private string? _error;
    private double _pendingStartSeconds;
    private bool _startWhenReady;

    // Gains: written by the UI/transport, read by the render thread.
    private float _volume = 1.0f;
    private float _karaokeGain = 0.5f;
    private float _duck = 1.0f;
    private float _karaokeSmooth = 0.5f;
    private double _pendingSyncOffsetMs;
    private IDeckGainSource? _gainSource;

    public DeckSource(int sampleRate, int channels)
    {
        _rate = sampleRate;
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _karaokeScratch = new float[channels * 8192];
        _sum = new SumProvider(this);
        _tempo = new TempoStage(_sum);
        Position = new DeckPosition(sampleRate);
        Commands = new RenderCommands();
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Render-thread command queue for this deck.</summary>
    public RenderCommands Commands { get; }

    /// <summary>The deck's clock (source seconds are ratio-invariant).</summary>
    public DeckPosition Position { get; }

    /// <summary>State as last published by the render thread.</summary>
    public DeckState State => _state;

    /// <summary>Path of the loaded primary stem (for the UI and for kill/duplicate checks).</summary>
    public string? TrackPath { get; private set; }

    /// <summary>Path of the loaded karaoke stem, if any.</summary>
    public string? KaraokePath { get; private set; }

    /// <summary>True when a karaoke stem is loaded.</summary>
    public bool HasKaraoke => _karaoke != null;

    /// <summary>
    /// Duration of the loaded track in seconds. Published when the file is opened,
    /// so it is valid as soon as <see cref="LoadAsync"/> completes — it does not
    /// wait for the render thread to swap the reader in (which needs a running
    /// engine). Zero only when the deck holds no track.
    /// </summary>
    public double DurationSeconds => _durationSeconds;

    /// <summary>Failure message when <see cref="State"/> is <see cref="DeckState.Failed"/>.</summary>
    public string? Error => _error;

    /// <summary>Audible position: counted after the stretcher, so it matches what is heard.</summary>
    public double PositionSeconds
    {
        get
        {
            double p = Position.SourceSeconds;

            // In split mode the mix stretcher is idle; the karaoke stage is the one
            // that produced the audible block. Both split stages share a ratio, so
            // either one's group delay describes the playhead.
            var stage = _splitActive ? _splitKaraoke : _tempo;
            if (stage is { Engaged: true })
            {
                // Subtract the stretcher's group delay: the audio leaving the deck
                // is that much older than the frames we have counted.
                p -= stage.LatencyFrames / (double)_rate;
            }
            return Math.Clamp(p, 0, Math.Max(0, _durationSeconds));
        }
    }

    /// <summary>Current tempo ratio (1.0 = original).</summary>
    public double TempoRatio => _splitActive ? (_splitKaraoke?.Ratio ?? _tempoRequested) : _tempo.Ratio;

    /// <summary>
    /// Karaoke split render mode. When true (and a karaoke stem is loaded) the
    /// engine reads <see cref="ReadKaraoke"/> and <see cref="ReadOriginal"/>
    /// instead of <see cref="Read"/>: each stem gets its own stretcher with the
    /// same ratio and phase so they can be routed to different buses, while the
    /// playhead is advanced exactly once per block.
    ///
    /// <para>With this false the deck behaves exactly as it always has: one
    /// stretcher over the summed stems, one read per block.</para>
    /// </summary>
    public bool KaraokeSplit
    {
        get => _splitRequested;
        set => _splitRequested = value;
    }

    /// <summary>True while the per-stem stretchers are the ones rendering (test/diagnostic hook).</summary>
    public bool IsSplitting => _splitActive;

    /// <summary>Engine frame index of the block being rendered; set by the mixer before reading.</summary>
    public long FrameCursor { get; set; }

    /// <summary>Per-frame gain from the transport (crossfader envelope).</summary>
    public IDeckGainSource? GainSource
    {
        get => _gainSource;
        set => _gainSource = value;
    }

    /// <summary>Fires on the render thread when the media reaches its end.</summary>
    public event Action<DeckSource>? Ended;

    /// <summary>Fires on the render thread when the deck fails (stretcher, device, unreadable file).</summary>
    public event Action<DeckSource, string>? Failed;

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Load a track. The file is opened and converted on a worker thread; the
    /// swap happens on the render thread so playback is never stalled by disk.
    /// <see cref="DurationSeconds"/> is published from the opened file, so it is
    /// valid when this task completes even if no block has rendered yet.
    /// </summary>
    public async Task LoadAsync(string primaryPath, string? karaokePath = null, double karaokeSyncOffsetMs = 0,
        double startSeconds = 0, bool playWhenReady = true)
    {
        Commands.Post(() => _state = DeckState.Loading);
        TrackPath = primaryPath;
        KaraokePath = karaokePath;

        Prepared prepared;
        try
        {
            prepared = await Task.Run(() => Prepared.Open(primaryPath, karaokePath, _rate, _channels, karaokeSyncOffsetMs))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"deck load failed: {System.IO.Path.GetFileName(primaryPath)}", ex);
            Commands.Post(() =>
            {
                _state = DeckState.Failed;
                _error = ex.Message;
            });
            throw;
        }

        _pendingStartSeconds = Math.Max(0, startSeconds);
        _startWhenReady = playWhenReady;

        // The file is open and its length known *here*, so publish the duration now
        // rather than only inside the posted Swap: a caller that has awaited this
        // task must never see 0 s for a file that loaded. Before this the duration
        // was render-thread state only, so a load that completed with no block
        // rendered in between (engine stopped, all outputs None, or a caller that
        // loads two decks and immediately schedules a transition) reported
        // DurationSeconds = 0 and every consumer treated the deck as empty — the
        // scheduler refused a perfectly valid hand-off for that reason. The reader
        // swap itself stays on the render thread; DurationSeconds is a reporting
        // field, not render state.
        _durationSeconds = prepared.DurationSeconds;

        Commands.Post(() => Swap(prepared));
    }

    /// <summary>Unload the deck (silence, then release the files on the render thread).</summary>
    public void Unload()
    {
        TrackPath = null;
        KaraokePath = null;
        Commands.Post(() =>
        {
            _primary?.Dispose();
            _karaoke?.Dispose();
            _primary = null;
            _karaoke = null;
            FlushStages();
            Position.SetTrack(0);
            _durationSeconds = 0;
            _state = DeckState.Empty;
            _error = null;
        });
    }

    public void Play() => Commands.Post(() =>
    {
        if (_primary == null) return;
        if (_state == DeckState.Ended) SeekInternal(0);
        _state = DeckState.Playing;
    });

    public void Pause() => Commands.Post(() =>
    {
        if (_state == DeckState.Playing) _state = DeckState.Paused;
    });

    public void Stop() => Commands.Post(() =>
    {
        if (_primary == null) return;
        SeekInternal(0);
        _state = DeckState.Loaded;
    });

    /// <summary>Seek to an absolute media position (seconds).</summary>
    public void Seek(double seconds) => Commands.Post(() => SeekInternal(seconds));

    /// <summary>Deck fader (0..1.2) — the mixer's own volume, separate from the crossfader.</summary>
    public void SetVolume(float volume) => _volume = Math.Clamp(volume, 0f, 1.2f);

    /// <summary>Karaoke sub-stem gain (0 = backing only, 1 = original vocal).</summary>
    public void SetKaraokeGain(float gain) => _karaokeGain = Math.Clamp(gain, 0f, 1f);

    /// <summary>Temporary attenuation for ducking (soundboard, monitor dim).</summary>
    public void SetDuck(float duck) => _duck = Math.Clamp(duck, 0f, 1f);

    /// <summary>Change the tempo ratio (key-locked). 1.0 bypasses the stretcher.</summary>
    public void SetTempo(double ratio)
    {
        _tempoRequested = Math.Clamp(ratio, 0.5, 2.0);
        _tempo.SetRatio(_tempoRequested);
        // Both split stages take the same ratio in the same block, so they stay
        // sample-aligned through a tempo change instead of drifting apart.
        _splitKaraoke?.SetRatio(_tempoRequested);
        _splitOriginal?.SetRatio(_tempoRequested);
    }

    /// <summary>Karaoke sync offset in ms (±5 s); re-applied on the render thread.</summary>
    public void SetKaraokeSyncOffset(double milliseconds) => Commands.Post(() =>
    {
        _pendingSyncOffsetMs = milliseconds;
        _karaoke?.SetSyncOffset(milliseconds);
    });

    // ------------------------------------------------------------------ render thread

    public int Read(float[] buffer, int offset, int count)
    {
        Commands.Drain();

        if (_splitActive) ExitSplit();

        return ReadMix(buffer, offset, count);
    }

    /// <summary>
    /// Karaoke mode read: the karaoke stem only, through its own stretcher. The
    /// deck clock is advanced here and only here (once per block), which is what
    /// keeps <see cref="ReadOriginal"/> from doubling the playhead.
    /// </summary>
    public int ReadKaraoke(float[] buffer, int offset, int count)
    {
        Commands.Drain();

        if (!TryEnterSplit()) return ReadMix(buffer, offset, count);

        return ReadSplit(buffer, offset, count, karaoke: true, advance: true);
    }

    /// <summary>
    /// Karaoke mode read: the original stem only, through its own stretcher, with
    /// the same ratio and phase as <see cref="ReadKaraoke"/>. Does not advance the
    /// clock. Silence unless karaoke split mode is active.
    /// </summary>
    public int ReadOriginal(float[] buffer, int offset, int count)
    {
        if (!_splitActive)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        return ReadSplit(buffer, offset, count, karaoke: false, advance: false);
    }

    private int ReadMix(float[] buffer, int offset, int count)
    {
        if (_state != DeckState.Playing || _primary == null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        if (_tempo.Failure != null)
        {
            _state = DeckState.Failed;
            _error = _tempo.Failure.Message;
            Failed?.Invoke(this, _error);
            Array.Clear(buffer, offset, count);
            return count;
        }

        try
        {
            // Sum + stretcher + per-frame gain in one pass.
            _tempo.Read(buffer, offset, count);

            int frames = count / _channels;
            ApplyGain(buffer, offset, frames);

            // DeckPosition.Advance takes the *inverse* rate (output frames per
            // media second). The stretcher's Tempo is the opposite: media consumed
            // per output second (0.92 = slower, 1 s of output eats 0.92 s of file).
            // Handing it the ratio directly over-advanced the clock by 1/ratio:
            // 3.0 s of output at 0.92x reported 3.0/0.92 = 3.261 s where the audio
            // had consumed exactly 2.760 s. That clock is the UI playhead, the
            // waveform cursor and end-of-track detection, so the deck ran ahead of
            // its own sound and ended early.
            Position.Advance(frames, _tempo.Ratio > 0 ? 1.0 / _tempo.Ratio : 1.0);

            if (Position.ReachedEnd())
            {
                _state = DeckState.Ended;
                Ended?.Invoke(this);
                // Keep emitting the tail of this block; the next block is silence.
            }
        }
        catch (Exception ex)
        {
            Log.Error("deck render failed", ex);
            _state = DeckState.Failed;
            _error = ex.Message;
            Failed?.Invoke(this, _error);
            Array.Clear(buffer, offset, count);
        }

        return count;
    }

    /// <summary>
    /// Enter split mode on the render thread. Both stages are flushed and given
    /// the same ratio so their first reads start at the same media frame; the
    /// mix stretcher is flushed too so leaving split mode cannot resume with stale
    /// history. Called only from <see cref="ReadKaraoke"/>.
    /// </summary>
    private bool TryEnterSplit()
    {
        if (!_splitRequested || _karaoke == null || _primary == null) return false;
        if (_splitActive) return true;

        _splitKaraoke ??= new TempoStage(new StemOnlyProvider(this, karaoke: true));
        _splitOriginal ??= new TempoStage(new StemOnlyProvider(this, karaoke: false));

        _splitKaraoke.SetRatio(_tempoRequested);
        _splitOriginal.SetRatio(_tempoRequested);
        _splitKaraoke.RequestFlush();
        _splitOriginal.RequestFlush();
        _tempo.RequestFlush();

        _splitActive = true;
        return true;
    }

    /// <summary>Leave split mode (back to the summed, single-stretcher path).</summary>
    private void ExitSplit()
    {
        _splitActive = false;
        FlushStages();
    }

    /// <summary>
    /// One stem through its own stretcher. Only <paramref name="advance"/> (the
    /// karaoke read) moves <see cref="Position"/>: two advances per block would
    /// run the deck clock at 2x and end every track early.
    /// </summary>
    private int ReadSplit(float[] buffer, int offset, int count, bool karaoke, bool advance)
    {
        var stage = karaoke ? _splitKaraoke! : _splitOriginal!;

        if (_state != DeckState.Playing || _primary == null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        if (stage.Failure != null)
        {
            _state = DeckState.Failed;
            _error = stage.Failure.Message;
            Failed?.Invoke(this, _error);
            Array.Clear(buffer, offset, count);
            return count;
        }

        try
        {
            stage.Read(buffer, offset, count);

            int frames = count / _channels;
            ApplyGain(buffer, offset, frames);

            if (advance)
            {
                Position.Advance(frames, stage.Ratio > 0 ? 1.0 / stage.Ratio : 1.0);

                if (Position.ReachedEnd())
                {
                    _state = DeckState.Ended;
                    Ended?.Invoke(this);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("deck render failed", ex);
            _state = DeckState.Failed;
            _error = ex.Message;
            Failed?.Invoke(this, _error);
            Array.Clear(buffer, offset, count);
        }

        return count;
    }

    /// <summary>Flush every stretcher so the next read starts from a clean line.</summary>
    private void FlushStages()
    {
        _tempo.RequestFlush();
        _splitKaraoke?.RequestFlush();
        _splitOriginal?.RequestFlush();
    }

    private void ApplyGain(float[] buffer, int offset, int frames)
    {
        // Karaoke gain is smoothed over ~5 ms so dragging the slider cannot click.
        float target = _karaokeGain;
        float step = Math.Abs(target - _karaokeSmooth) < 1e-4f
            ? 0f
            : 1f / Math.Max(1f, 0.005f * _rate);

        for (int f = 0; f < frames; f++)
        {
            if (_karaokeSmooth < target) _karaokeSmooth = Math.Min(target, _karaokeSmooth + step);
            else if (_karaokeSmooth > target) _karaokeSmooth = Math.Max(target, _karaokeSmooth - step);

            float gain = _gainSource?.GainAt(FrameCursor + f) ?? _volume;
            int baseIdx = offset + f * _channels;
            for (int c = 0; c < _channels; c++) buffer[baseIdx + c] *= gain;
        }
    }

    private void SeekInternal(double seconds)
    {
        if (_primary == null) return;
        double clamped = Math.Clamp(seconds, 0, Math.Max(0, _durationSeconds - 0.001));
        _primary.Seek(TimeSpan.FromSeconds(clamped));
        if (_karaoke != null)
        {
            // Karaoke offset is relative to the primary; a negative offset trims
            // its head, so its seek target shifts by the same amount.
            double karaokeSeconds = clamped - _pendingSyncOffsetMs / 1000.0;
            _karaoke.Seek(TimeSpan.FromSeconds(Math.Max(0, karaokeSeconds)));
            _karaoke.SetSyncOffset(Math.Max(0, _pendingSyncOffsetMs));
        }
        FlushStages();
        Position.Seek(clamped);
    }

    private void Swap(Prepared prepared)
    {
        _primary?.Dispose();
        _karaoke?.Dispose();

        _primary = prepared.Primary;
        _karaoke = prepared.Karaoke;
        _pendingSyncOffsetMs = prepared.SyncOffsetMs;
        _karaoke?.SetSyncOffset(Math.Max(0, prepared.SyncOffsetMs));

        _durationSeconds = prepared.DurationSeconds;
        Position.SetTrack(_durationSeconds);
        FlushStages();
        _error = null;

        if (_pendingStartSeconds > 0) SeekInternal(_pendingStartSeconds);
        _state = _startWhenReady ? DeckState.Playing : DeckState.Loaded;
    }

    /// <summary>A file pair opened and normalised off the render thread.</summary>
    private sealed record Prepared(
        StemReader Primary,
        StemReader? Karaoke,
        double DurationSeconds,
        double SyncOffsetMs)
    {
        public static Prepared Open(string primaryPath, string? karaokePath, int rate, int channels, double syncOffsetMs)
        {
            var primary = StemReader.Open(primaryPath, rate, channels);
            StemReader? karaoke = null;
            try
            {
                if (!string.IsNullOrEmpty(karaokePath) && System.IO.File.Exists(karaokePath))
                    karaoke = StemReader.Open(karaokePath, rate, channels);
            }
            catch (Exception ex)
            {
                // A broken karaoke file must not stop the original from playing.
                Log.Warn($"karaoke stem failed to open ({karaokePath}): {ex.Message}");
                karaoke?.Dispose();
                karaoke = null;
            }

            return new Prepared(primary, karaoke, primary.DurationSeconds, syncOffsetMs);
        }
    }

    /// <summary>
    /// Sums the primary and karaoke stems into one provider for the stretcher.
    /// Deliberately hand-written instead of <c>MixingSampleProvider</c>: that type
    /// drops inputs on a zero read and re-adds them at a different position, which
    /// is exactly the phase discontinuity a beat-matched mix cannot tolerate.
    /// </summary>
    private sealed class SumProvider : ISampleProvider
    {
        private readonly DeckSource _deck;

        public SumProvider(DeckSource deck) => _deck = deck;

        public WaveFormat WaveFormat => _deck.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var primary = _deck._primary;
            if (primary == null)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            Samples.ReadFull(primary, buffer, offset, count);

            var karaoke = _deck._karaoke;
            if (karaoke == null) return count;

            if (_deck._karaokeScratch.Length < count)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            Samples.ReadFull(karaoke, _deck._karaokeScratch, 0, count);
            Samples.AddScaled(buffer, _deck._karaokeScratch, offset, count, _deck._karaokeSmooth);
            return count;
        }
    }

    /// <summary>
    /// One stem for a split-render stretcher. Reads only the primary or only the
    /// karaoke reader, zeros when it is missing (never a short read: the stretcher
    /// treats that as the end of the input).
    /// </summary>
    private sealed class StemOnlyProvider : ISampleProvider
    {
        private readonly DeckSource _deck;
        private readonly bool _karaoke;

        public StemOnlyProvider(DeckSource deck, bool karaoke)
        {
            _deck = deck;
            _karaoke = karaoke;
        }

        public WaveFormat WaveFormat => _deck.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var reader = _karaoke ? _deck._karaoke : _deck._primary;
            if (reader == null)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            Samples.ReadFull(reader, buffer, offset, count);
            return count;
        }
    }

    public void Dispose() => Unload();
}

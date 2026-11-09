using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using JockeyBox.Tools;

namespace JockeyBox.Audio.Endpoints;

/// <summary>
/// What the engine needs from a microphone: blocks on demand plus a drift hook.
///
/// <para>An interface rather than a concrete <see cref="MicCapture"/> so the
/// mixer's voice path can be rendered offline against a synthetic source in the
/// tests - the mic is the only input that normally requires hardware.</para>
/// </summary>
public interface IMicSource : ISampleProvider
{
    /// <summary>Drift correction, called by the engine roughly once a second.</summary>
    void Maintain(int blockFrames);
}

/// <summary>
/// The talk-over microphone: one WASAPI <i>capture</i> endpoint, converted to the
/// engine's 48 kHz stereo float format and pushed into a bounded ring that the
/// render thread pulls from.
///
/// <para><b>Why a ring and not a callback into the mix.</b> The capture device
/// and the render device are two clocks (a USB mic can run tens of ppm off the
/// motherboard codec). The engine renders 480-frame blocks on its own thread and
/// must never wait for the microphone: <see cref="Read"/> takes whatever is
/// buffered and pads the rest with silence, so a late, stalled or unplugged
/// microphone is a quiet gap in the voice - never a stutter in the music.
/// <see cref="JitterRing.Maintain"/> trims or pads the depth on the same
/// schedule the output sinks use.</para>
///
/// <para><b>Capture is its own thread.</b> <see cref="WasapiCapture"/>'s
/// <c>DataAvailable</c> only appends raw bytes to a bounded
/// <see cref="BufferedWaveProvider"/> (dropping on overflow); a dedicated thread
/// reads that buffer, resamples to <see cref="Rate"/>, maps mono to both
/// channels and writes the ring. Nothing on the audio path allocates per block
/// and nothing there blocks.</para>
///
/// <para><b>The Windows recording level is never touched.</b> No
/// <c>IAudioEndpointVolume</c>, no device property writes: <c>MicLevel</c> is an
/// app-domain gain applied by <see cref="Graph.AudioEngine"/> before the sum.</para>
/// </summary>
public sealed class MicCapture : IMicSource, IDisposable
{
    /// <summary>Engine sample rate the microphone is converted to.</summary>
    public const int Rate = 48000;

    /// <summary>Engine channel count (mono mics are duplicated to both channels).</summary>
    public const int Channels = 2;

    /// <summary>Frames converted per capture-thread pass (10 ms).</summary>
    private const int ChunkFrames = Rate / 100;

    private readonly object _gate = new();
    private readonly JitterRing _ring = new(Rate, Channels, 1000)
    {
        // 60 ms of voice buffered: enough to absorb a scheduler hiccup, small
        // enough that the voice stays with the picture in OBS.
        TargetFrames = Rate * 60 / 1000,
        LowFrames = Rate * 30 / 1000,
        HighFrames = Rate * 250 / 1000,
    };

    private readonly float[] _scratch = new float[ChunkFrames * 8];
    private readonly string _deviceId;

    private MMDevice? _device;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _pipeline;
    private Thread? _thread;
    private volatile bool _running;
    private int _disposed;
    private int _lostLogged;

    /// <param name="deviceId">Capture endpoint id; must resolve, otherwise <see cref="Start"/> fails.</param>
    public MicCapture(string? deviceId = null)
    {
        _deviceId = deviceId ?? "";
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);

    /// <summary>The endpoint's friendly name, once started; empty before that.</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>The capture device's native mix rate (what the device opened at).</summary>
    public int DeviceRate { get; private set; }

    /// <summary>Frames the render thread could not get from the ring (voice gaps).</summary>
    public long UnderrunFrames => _ring.UnderrunFrames;

    /// <summary>Voice buffered right now, in milliseconds.</summary>
    public double BufferedMs => _ring.DepthFrames * 1000.0 / Rate;

    /// <summary>Drift corrections applied to the voice ring (drop/insert events).</summary>
    public long DriftCorrections => _ring.DroppedBlocks + _ring.InsertedBlocks;

    /// <summary>False once the device stopped on its own (unplugged or driver loss).</summary>
    public bool IsCapturing => _running;

    /// <summary>
    /// Open the endpoint and start capturing. Returns false (without throwing)
    /// when the saved device is gone - the caller then leaves the mic at None.
    /// </summary>
    public bool Start(string? deviceId = null)
    {
        ThrowIfDisposed();

        string id = string.IsNullOrWhiteSpace(deviceId) ? _deviceId : deviceId;
        if (string.IsNullOrWhiteSpace(id)) return false;

        MMDevice? device = null;
        try
        {
            device = new DeviceService().ResolveCaptureDevice(id);
            if (device == null)
            {
                // The caller (DualDeckTransport) owns the user-facing "not
                // available" line, so a missing device is logged once per route
                // pass there, not twice.
                return false;
            }

            var capture = new WasapiCapture(device);
            var format = capture.WaveFormat;

            // Bounded and lossy on purpose: if this thread stalls, old voice is
            // dropped instead of the buffer growing into seconds of latency.
            var buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };

            ISampleProvider samples = buffer.ToSampleProvider();
            if (samples.WaveFormat.SampleRate != Rate)
                samples = new WdlResamplingSampleProvider(samples, Rate);

            // Mono headset mic -> both channels; a stereo interface passes through.
            samples = new ChannelMapSampleProvider(samples, Channels, duplicateMono: true);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            _device = device;
            _capture = capture;
            _buffer = buffer;
            _pipeline = samples;
            DeviceName = device.FriendlyName;
            DeviceRate = format.SampleRate;

            capture.StartRecording();

            _running = true;
            var thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "JockeyBox mic capture",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread = thread;
            thread.Start();

            Log.Info($"mic: {DeviceName} @ {DeviceRate} Hz -> engine {Rate} Hz");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"mic could not be opened: {ex.Message}");
            Cleanup(device);
            return false;
        }
    }

    /// <summary>
    /// Pull one block for the mixer. Never blocks and never returns less than
    /// asked: an empty ring yields silence (a gap in the voice, not a stall).
    /// </summary>
    public int Read(float[] buffer, int offset, int count) => _ring.Read(buffer, offset, count);

    /// <summary>Drift correction, called by the engine ~once a second.</summary>
    public void Maintain(int blockFrames) => _ring.Maintain(blockFrames);

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var buffer = _buffer;
        if (buffer == null || e.BytesRecorded <= 0) return;

        try
        {
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (InvalidOperationException)
        {
            // Buffer was torn down while the device was still delivering.
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _running = false;

        // A stopped wasapi capture never restarts: report it once and leave the
        // voice silent, the music keeps playing off the same render clock.
        if (e.Exception != null && Interlocked.Exchange(ref _lostLogged, 1) == 0)
            Log.Warn($"mic device lost: {DeviceName}");
    }

    private void CaptureLoop()
    {
        // The staging buffer is ReadFully: when the device is quiet it hands back
        // silence rather than nothing. So the loop must consume at *wall-clock*
        // rate, not as fast as it can - a 2 ms sleep per 10 ms chunk would push
        // four seconds of silence into the ring every second and leave the voice
        // buried behind a full buffer (audible as lip-sync drift in OBS).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long dueMs = 0;
        int chunkMs = ChunkFrames * 1000 / Rate;

        while (_running)
        {
            ISampleProvider? pipeline = _pipeline;
            if (pipeline == null) break;

            int want = ChunkFrames * pipeline.WaveFormat.Channels;
            if (_scratch.Length < want) break;

            int got;
            try
            {
                got = pipeline.Read(_scratch, 0, want);
            }
            catch (Exception ex)
            {
                _running = false;
                if (Interlocked.Exchange(ref _lostLogged, 1) == 0)
                    Log.Warn($"mic device lost: {DeviceName} ({ex.Message})");
                break;
            }

            if (got <= 0)
            {
                // Nothing captured yet (or the device went quiet): pace the loop
                // and let the ring underrun into silence.
                Thread.Sleep(5);
                continue;
            }

            int frames = got / Channels;
            if (frames > 0) _ring.Write(_scratch, 0, frames);

            // Guard against a burst: the ring is the only thing that may grow.
            if (_ring.DepthFrames > _ring.HighFrames) _ring.Maintain(ChunkFrames);

            dueMs += chunkMs;
            long behind = clock.ElapsedMilliseconds - dueMs;
            if (behind > 500)
            {
                // A long stall (scheduler, sleep/resume) is not worth catching up
                // on: skip the missed chunks instead of racing the device.
                clock.Restart();
                dueMs = 0;
            }
            else if (behind < 0)
            {
                Thread.Sleep((int)Math.Min(-behind, 20));
            }
        }
    }

    private void Cleanup(MMDevice? device)
    {
        _running = false;

        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { /* already stopped */ }
            _capture.Dispose();
            _capture = null;
        }

        _pipeline = null;
        _buffer = null;

        device?.Dispose();
        if (ReferenceEquals(_device, device)) _device = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _running = false;

        var thread = _thread;
        _thread = null;
        if (thread != null && thread != Thread.CurrentThread)
        {
            // The loop exits within one read (<= 10 ms); do not block disposal on it.
            thread.Join(200);
        }

        Cleanup(_device);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MicCapture));
    }
}

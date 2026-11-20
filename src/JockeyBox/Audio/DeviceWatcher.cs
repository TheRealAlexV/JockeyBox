using System;
using System.Threading;
using JockeyBox.Tools;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace JockeyBox.Audio;

/// <summary>What happened to an endpoint. Flags, so a burst coalesces into one value.</summary>
[Flags]
public enum DeviceChangeKind
{
    Added = 1,
    Removed = 2,
    StateChanged = 4,
    DefaultChanged = 8,
}

/// <summary>
/// One endpoint notification, as the rebuild consumer sees it. The flow is
/// <see cref="DataFlow.All"/> when Windows did not name one (add/remove/state).
/// </summary>
public readonly record struct DeviceChange(DeviceChangeKind Kind, DataFlow Flow, string? DeviceId)
{
    public override string ToString() => $"{Kind} flow={Flow} id={DeviceId ?? "(none)"}";
}

/// <summary>
/// The seam that makes teardown provable: the real implementation owns the one
/// long-lived <see cref="MMDeviceEnumerator"/>, and a test double can count
/// <c>Register</c> against <c>Unregister</c> without touching COM.
/// </summary>
public interface IEndpointNotificationRegistrar : IDisposable
{
    void Register(IMMNotificationClient client);
    void Unregister(IMMNotificationClient client);
}

/// <summary>The process's single long-lived enumerator, kept alive for callbacks only.</summary>
public sealed class WasapiEndpointNotificationRegistrar : IEndpointNotificationRegistrar
{
    private MMDeviceEnumerator? _enumerator;

    // Activated on first use, not in the constructor: a headless session with no
    // audio service must still be able to build the view model.
    private MMDeviceEnumerator Enumerator => _enumerator ??= new MMDeviceEnumerator();

    public void Register(IMMNotificationClient client) =>
        Enumerator.RegisterEndpointNotificationCallback(client);

    public void Unregister(IMMNotificationClient client) =>
        _enumerator?.UnregisterEndpointNotificationCallback(client);

    public void Dispose()
    {
        _enumerator?.Dispose();
        _enumerator = null;
    }
}

/// <summary>
/// The COM callback surface. Windows calls these on its own COM thread, so
/// nothing here may touch a collection or the view: it only translates and
/// raises <see cref="Changed"/>, and an exception must never escape back into
/// MMDevAPI.
/// </summary>
public sealed class DeviceNotificationClient : IMMNotificationClient
{
    public event Action<DeviceChange>? Changed;

    public void OnDeviceAdded(string pwstrDeviceId) =>
        Raise(new DeviceChange(DeviceChangeKind.Added, DataFlow.All, pwstrDeviceId));

    public void OnDeviceRemoved(string pwstrDeviceId) =>
        Raise(new DeviceChange(DeviceChangeKind.Removed, DataFlow.All, pwstrDeviceId));

    public void OnDeviceStateChanged(string pwstrDeviceId, DeviceState dwNewState) =>
        Raise(new DeviceChange(DeviceChangeKind.StateChanged, DataFlow.All, pwstrDeviceId));

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string pwstrDefaultDeviceId) =>
        Raise(new DeviceChange(DeviceChangeKind.DefaultChanged, flow, pwstrDefaultDeviceId));

    // Property-value changes are not a device appearing or leaving; a rename is
    // already covered by the state call Windows sends for it.
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }

    private void Raise(DeviceChange change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception ex)
        {
            Log.Warn($"Device notification handler failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Collapses a burst of notifications into one pending change. An add, a remove
/// and a default change during one dock/headset connect become a single rebuild
/// carrying the union of the kinds.
/// </summary>
public sealed class DeviceChangeCoalescer
{
    private readonly object _gate = new();
    private DeviceChangeKind _kinds;
    private DataFlow _flow = DataFlow.All;
    private string? _deviceId;
    private bool _scheduled;

    /// <summary>
    /// Record a change. True means this is the first of the burst, so the caller
    /// schedules the one rebuild; false means the burst already has one.
    /// </summary>
    public bool Offer(DeviceChange change)
    {
        lock (_gate)
        {
            _kinds |= change.Kind;
            // Prefer a named flow (default-change names one) over "any".
            if (change.Flow != DataFlow.All) _flow = change.Flow;
            _deviceId ??= change.DeviceId;
            if (_scheduled) return false;
            _scheduled = true;
            return true;
        }
    }

    /// <summary>Take the accumulated burst and re-arm for the next one.</summary>
    public DeviceChange Take()
    {
        lock (_gate)
        {
            var batch = new DeviceChange(_kinds, _flow, _deviceId);
            _kinds = 0;
            _flow = DataFlow.All;
            _deviceId = null;
            _scheduled = false;
            return batch;
        }
    }
}

/// <summary>
/// Watches Windows for endpoint changes and raises <see cref="DevicesChanged"/>
/// once per burst, on the UI context.
///
/// <para>Only lists are affected: this class never touches the audio graph, so an
/// unrelated device arriving cannot restart playback or move a route.</para>
///
/// <para>Registration is explicit (<see cref="Start"/>) and every start is paired
/// with an <see cref="Unregister"/> in <see cref="Stop"/>/<see cref="Dispose"/>.
/// Handing a notification callback back at shutdown matters: MMDevAPI holds a
/// reference to the client until it is unregistered.</para>
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly IEndpointNotificationRegistrar _registrar;
    private readonly DeviceNotificationClient _client = new();
    private readonly DeviceChangeCoalescer _coalescer = new();
    private readonly SynchronizationContext? _ui;
    private readonly Func<Action, IDisposable> _schedule;
    private IDisposable? _pending;
    private bool _disposed;

    /// <summary>Raised on <c>_ui</c> with the union of the burst's changes.</summary>
    public event Action<DeviceChange>? DevicesChanged;

    /// <param name="ui">The UI context to marshal to; defaults to the creating thread's.</param>
    /// <param name="debounce">Quiet period before one rebuild. Defaults to 300 ms.</param>
    /// <param name="schedule">
    /// One-shot timer seam: run the action after the debounce. Defaults to a
    /// <see cref="Timer"/>; tests pass an immediate one.
    /// </param>
    public DeviceWatcher(IEndpointNotificationRegistrar registrar,
                         SynchronizationContext? ui = null,
                         TimeSpan? debounce = null,
                         Func<Action, IDisposable>? schedule = null)
    {
        _registrar = registrar;
        _ui = ui ?? SynchronizationContext.Current;
        var delay = debounce ?? TimeSpan.FromMilliseconds(300);
        _schedule = schedule ?? (action =>
            new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan));
        _client.Changed += OnComChange;
    }

    /// <summary>True between a successful <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>The real WASAPI watcher, marshalling to the calling (UI) thread.</summary>
    public static DeviceWatcher CreateWasapi(SynchronizationContext? ui = null) =>
        new(new WasapiEndpointNotificationRegistrar(), ui);

    /// <summary>
    /// Register the callback. Idempotent, and a failure is logged rather than
    /// thrown: a headless session with no audio service must still launch.
    /// </summary>
    public void Start()
    {
        if (IsRunning || _disposed) return;
        try
        {
            _registrar.Register(_client);
            IsRunning = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Device notifications unavailable: " + ex.Message);
        }
    }

    /// <summary>Unregister the callback. Safe to call when not running.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        try
        {
            _registrar.Unregister(_client);
        }
        catch (Exception ex)
        {
            Log.Warn("Device notification unregister failed: " + ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void OnComChange(DeviceChange change)
    {
        if (_disposed) return;
        if (!_coalescer.Offer(change)) return;

        try
        {
            _pending?.Dispose();
            _pending = _schedule(Flush);
        }
        catch (Exception ex)
        {
            Log.Warn("Device change scheduling failed: " + ex.Message);
        }
    }

    private void Flush()
    {
        if (_disposed) return;
        var batch = _coalescer.Take();
        try
        {
            if (_ui == null)
            {
                Raise(batch);
                return;
            }
            _ui.Post(_ => Raise(batch), null);
        }
        catch (Exception ex)
        {
            Log.Warn("Device change marshal failed: " + ex.Message);
        }
    }

    private void Raise(DeviceChange batch)
    {
        // A rebuild that throws must not take the app down from a queued post.
        try
        {
            DevicesChanged?.Invoke(batch);
        }
        catch (Exception ex)
        {
            Log.Warn("Device change rebuild failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _client.Changed -= OnComChange;
        _pending?.Dispose();
        _pending = null;
        _registrar.Dispose();
    }
}

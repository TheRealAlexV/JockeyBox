using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using JockeyBox.Audio;
using JockeyBox.Playback;
using JockeyBox.Settings;
using JockeyBox.ViewModels;
using JockeyBox.Views;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The endpoint-notification path, headless. A fake registrar stands in for the
/// one long-lived <see cref="MMDeviceEnumerator"/> so registration, marshalling,
/// coalescing and teardown can be counted; the rebuild assertions then run
/// through the real view models and the real choice-list builder.
///
/// <para><b>What this cannot prove.</b> No test here plugs in hardware, so the
/// Windows-side event delivery itself is unexercised: what is proven is that a
/// notification reaches the rebuild on the UI thread, that the rebuild changes
/// only lists, and that teardown unregisters.</para>
/// </summary>
[Collection(nameof(UiThreadCollection))]
public sealed class DeviceWatcherTests
{
    // ---- seams --------------------------------------------------------------

    /// <summary>Counts register/unregister instead of touching MMDevAPI.</summary>
    private sealed class FakeRegistrar : IEndpointNotificationRegistrar
    {
        public IMMNotificationClient? Client { get; private set; }
        public int Registers { get; private set; }
        public int Unregisters { get; private set; }
        public int Disposals { get; private set; }

        public void Register(IMMNotificationClient client)
        {
            Client = client;
            Registers++;
        }

        public void Unregister(IMMNotificationClient client) => Unregisters++;

        public void Dispose() => Disposals++;
    }

    /// <summary>A UI context that queues posts, so "did it marshal?" is observable.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int Posted { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posted++;
            _queue.Enqueue((d, state));
        }

        /// <summary>Run what was posted, on the calling thread. Returns the count.</summary>
        public int Drain()
        {
            int n = 0;
            while (_queue.Count > 0)
            {
                var (callback, state) = _queue.Dequeue();
                callback(state);
                n++;
            }
            return n;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>Debounce seam that fires at once, for tests that do not measure the delay.</summary>
    private static IDisposable RunNow(Action action)
    {
        action();
        return new NoopDisposable();
    }

    private static bool WaitFor(Func<bool> condition, int milliseconds = 10_000)
    {
        for (int i = 0; i < milliseconds / 10; i++)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    // ---- wiring and teardown ------------------------------------------------

    [Fact]
    public void AChangeIsRaisedOnTheUiContextNotOnTheComThread()
    {
        var registrar = new FakeRegistrar();
        var ui = new RecordingContext();
        using var watcher = new DeviceWatcher(registrar, ui, TimeSpan.Zero, RunNow);
        watcher.Start();
        Assert.NotNull(registrar.Client);

        int raised = 0;
        int raisedOn = 0;
        watcher.DevicesChanged += _ =>
        {
            raised++;
            raisedOn = Environment.CurrentManagedThreadId;
        };

        // Windows calls the callback on its own COM thread; a burst must not
        // touch a collection from there.
        var comThread = new Thread(() => registrar.Client!.OnDeviceAdded("dev-new"));
        comThread.Start();
        comThread.Join();

        Assert.Equal(0, raised);
        Assert.Equal(1, ui.Posted);

        Assert.Equal(1, ui.Drain());
        Assert.Equal(1, raised);
        Assert.Equal(Environment.CurrentManagedThreadId, raisedOn);
    }

    [Fact]
    public void ABurstOfNotificationsBecomesOneRebuildCarryingEveryKind()
    {
        var registrar = new FakeRegistrar();
        var ui = new RecordingContext();
        var scheduled = new List<Action>();
        using var watcher = new DeviceWatcher(registrar, ui, TimeSpan.Zero,
            action =>
            {
                scheduled.Add(action);
                return new NoopDisposable();
            });
        watcher.Start();

        DeviceChange? seen = null;
        int rebuilds = 0;
        watcher.DevicesChanged += change =>
        {
            seen = change;
            rebuilds++;
        };

        registrar.Client!.OnDeviceAdded("a");
        registrar.Client!.OnDeviceRemoved("b");
        registrar.Client!.OnDeviceStateChanged("c", DeviceState.Active);
        registrar.Client!.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "d");

        // One dock/headset connect is a burst: one scheduled rebuild, not four.
        Assert.Single(scheduled);
        Assert.Equal(0, ui.Posted);

        scheduled[0]();
        Assert.Equal(1, ui.Drain());
        Assert.Equal(1, rebuilds);
        Assert.NotNull(seen);
        Assert.Equal(DeviceChangeKind.Added | DeviceChangeKind.Removed |
                     DeviceChangeKind.StateChanged | DeviceChangeKind.DefaultChanged, seen!.Value.Kind);
        Assert.Equal(DataFlow.Render, seen.Value.Flow);

        // Re-armed: the next burst schedules again rather than being swallowed.
        registrar.Client!.OnDeviceAdded("e");
        Assert.Equal(2, scheduled.Count);
    }

    [Fact]
    public void TeardownUnregistersTheCallbackExactlyOnce()
    {
        var registrar = new FakeRegistrar();
        using var watcher = new DeviceWatcher(registrar, new RecordingContext(), TimeSpan.Zero, RunNow);
        watcher.Start();

        Assert.True(watcher.IsRunning);
        Assert.Equal(1, registrar.Registers);
        Assert.NotNull(registrar.Client);

        watcher.Stop();
        Assert.False(watcher.IsRunning);
        Assert.Equal(1, registrar.Unregisters);

        // Start after Stop registers again, and Dispose pairs the second one.
        watcher.Start();
        Assert.Equal(2, registrar.Registers);
        watcher.Dispose();

        Assert.Equal(2, registrar.Unregisters);
        Assert.Equal(1, registrar.Disposals);

        watcher.Dispose();
        Assert.Equal(2, registrar.Unregisters);   // idempotent, never double-unregisters
    }

    // ---- the lists themselves ----------------------------------------------

    [Fact]
    public void AnAddedEndpointAppearsInTheDeckChoosersWithoutAReSettings()
    {
        var registrar = new FakeRegistrar();
        var outputs = new List<AudioDeviceInfo> { new("dev-a", "Built-in") };
        MainViewModel? vm = null;
        DeviceWatcher? watcher = null;

        UiThread.Run(() =>
        {
            watcher = new DeviceWatcher(registrar, SynchronizationContext.Current, TimeSpan.Zero, RunNow);
            vm = new MainViewModel(watcher, () => outputs.ToList());
            // What MainWindow does at construction and on Loaded: arms the
            // monitor list (it follows the deck combo) and the deck UI hooks.
            vm.InitializeMonitor();
            vm.InitializeDeckUi();
            vm.StartDeviceWatch();
        });

        try
        {
            // The device appears in the system, then Windows says so.
            outputs.Add(new AudioDeviceInfo("dev-new", "USB headset"));
            registrar.Client!.OnDeviceAdded("dev-new");

            string seen = "";
            bool appears = WaitFor(() => UiThread.Run(() =>
            {
                seen = $"A=[{string.Join(",", vm!.DeckA.Devices.Select(d => d.Id))}] " +
                       $"B=[{string.Join(",", vm!.DeckB.Devices.Select(d => d.Id))}] " +
                       $"M=[{string.Join(",", vm!.MonitorDeviceChoices.Select(d => d.Id))}]";
                return vm!.DeckA.Devices.Any(d => d.Id == "dev-new") &&
                       vm!.DeckB.Devices.Any(d => d.Id == "dev-new") &&
                       vm!.MonitorDeviceChoices.Any(d => d.Id == "dev-new");
            }));
            Assert.True(appears, seen);

            UiThread.Run(() =>
            {
                // Only lists changed: both routes are still their saved values.
                // None is the "" row on the deck side, null in settings.
                Assert.Equal(vm!.SettingsRef.MainOutputDeviceId ?? "", vm.DeckA.SelectedDevice?.Id ?? "");
                Assert.Equal(vm.SettingsRef.KaraokeAidOutputDeviceId ?? "", vm.DeckB.SelectedDevice?.Id ?? "");
            });
        }
        finally
        {
            UiThread.Run(() => watcher!.Dispose());
        }
    }

    [Fact]
    public void ARoutedDeviceThatLeavesStaysSelectedAndSaysNotConnected()
    {
        UiThread.Run(() =>
        {
            var devices = new DeviceService();
            using var transport = new DualDeckTransport(
                new DeckChannel("Deck A", devices), new DeckChannel("Deck B", devices));

            string? route = "dev-live";
            using var deck = new DeckViewModel("DECK A", PlaybackDeck.A, transport.DeckA, transport,
                () => { }, _ => 0, (_, _) => { }, () => route);

            // It is routed and present.
            deck.RefreshDevices(new[] { new AudioDeviceInfo("dev-live", "Interface") });
            Assert.Equal("dev-live", deck.SelectedDevice?.Id);

            // An unrelated device arrives: the route is untouched and both show.
            deck.RefreshDevices(new[]
            {
                new AudioDeviceInfo("dev-live", "Interface"),
                new AudioDeviceInfo("dev-other", "Webcam"),
            });
            Assert.Equal("dev-live", deck.SelectedDevice?.Id);
            Assert.Contains(deck.Devices, d => d.Id == "dev-other");

            // The routed device is unplugged: still selected, named as missing.
            deck.RefreshDevices(new[] { new AudioDeviceInfo("dev-other", "Webcam") });
            Assert.Equal("dev-live", deck.SelectedDevice?.Id);
            Assert.Equal(OutputDeviceChoices.NotConnectedName, deck.SelectedDevice?.FriendlyName);
            Assert.Equal("dev-live", route);
        });
    }

    [Fact]
    public void TheSettingsPickersRepopulateWhileTheDialogIsOpen()
    {
        var outputs = new List<AudioDeviceInfo> { new("dev-a", "Built-in") };
        var registrar = new FakeRegistrar();
        SettingsWindow? window = null;
        DeviceWatcher? watcher = null;

        UiThread.Run(() =>
        {
            // The dialog's XAML names brushes from Themes/Dark.xaml, which the
            // test host's bare Application does not merge the way App.xaml does.
            var app = Application.Current!;
            if (app.Resources.MergedDictionaries.Count == 0)
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/JockeyBox;component/Themes/Dark.xaml", UriKind.Relative),
                });

            watcher = new DeviceWatcher(registrar, SynchronizationContext.Current,
                TimeSpan.Zero, RunNow);
            watcher.Start();
            window = new SettingsWindow(AppSettings.Load(), deviceWatcher: watcher,
                outputDevices: () => outputs.ToList());
        });

        try
        {
            outputs.Add(new AudioDeviceInfo("dev-new", "USB headset"));
            registrar.Client!.OnDeviceAdded("dev-new");

            Assert.True(WaitFor(() => UiThread.Run(AllBoxesShowNewDevice)));
        }
        finally
        {
            UiThread.Run(() =>
            {
                watcher!.Dispose();
                window!.Close();
            });
        }

        bool AllBoxesShowNewDevice()
        {
            foreach (var name in new[]
                     {
                         "MainOutputBox", "KaraokeAidOutputBox", "RecordingOutputBox",
                         "SoundboardOutputBox", "MonitorOutputBox",
                     })
            {
                if (window!.FindName(name) is not ComboBox box) return false;
                if (!box.Items.OfType<ComboBoxItem>()
                        .Any(i => Equals(i.Tag as string, "dev-new"))) return false;
            }
            return true;
        }
    }
}

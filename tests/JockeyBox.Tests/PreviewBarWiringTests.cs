using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using JockeyBox.ViewModels;
using JockeyBox.Views;

namespace JockeyBox.Tests;

/// <summary>
/// Wiring proof for the preview strip's seek slider.
///
/// <para><b>Why this exists.</b> <c>--preview-check</c> calls
/// <see cref="IPreviewTransport.Seek"/> directly, so it stays green even when the
/// control's own gesture brackets are disconnected. The operator's bug - a seek
/// bar nobody can drag - lives exactly in that gap, so the real control is driven
/// here: real <see cref="PreviewBar"/>, real template, real routed events on the
/// <c>SeekBar</c> slider, and a recording fake behind it.</para>
///
/// <para>Runs on <see cref="UiThread"/>: the suite's existing STA thread with a
/// process-wide <see cref="Application"/>. The strip's XAML pulls StaticResources
/// from <c>Themes/Dark.xaml</c>, which that Application does not load (it is
/// created bare), so <see cref="EnsureThemeResources"/> merges them once.</para>
/// </summary>
public sealed class PreviewBarWiringTests
{
    private const double Duration = 300.0;
    private const double StartPosition = 50.0;

    [Fact]
    public void Releasing_the_drag_bracket_commits_exactly_one_seek()
    {
        RunOnUi(() =>
        {
            var (_, slider, transport) = BuildSeekable();

            Press(slider);                                        // opens the bracket
            slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            slider.Value = 123.0;
            Assert.Empty(transport.SeekCalls);                    // moving the thumb is not a seek
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

            // A real drag also sends the preview mouse-up after DragCompleted.
            // That pair must not commit a second time.
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            });

            Assert.Equal(new[] { 123.0 }, transport.SeekCalls);
            Assert.Equal(new[] { true }, transport.PlayingCalls);
        });
    }

    [Fact]
    public void Mouse_up_after_a_press_commits_one_seek_at_the_slider_value()
    {
        RunOnUi(() =>
        {
            var (_, slider, transport) = BuildSeekable();

            Press(slider);
            slider.Value = 200.0;
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            });

            Assert.Equal(new[] { 200.0 }, transport.SeekCalls);
            Assert.Equal(new[] { true }, transport.PlayingCalls);
        });
    }

    /// <summary>
    /// The operator's actual gesture: a click on the track, not the thumb.
    ///
    /// <para>This is deliberately not <see cref="Press"/>. <c>IsMoveToPointEnabled</c>
    /// stays on, which is what production runs, so the Slider's own class handler
    /// runs first and marks the press handled - exactly the ordering that swallowed
    /// the strip's instance handler and made a click do nothing. The assert just
    /// after the press is the guard against a vacuous pass: if WPF ever stops
    /// handling that press, this test stops proving anything and says so.</para>
    /// </summary>
    [Fact]
    public void A_click_on_the_track_commits_one_seek_even_though_the_slider_handles_the_press()
    {
        RunOnUi(() =>
        {
            var (_, slider, transport) = BuildSeekable();

            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            };
            slider.RaiseEvent(press);
            Assert.True(press.Handled,
                "move-to-point must mark the press handled, or this test cannot prove the ordering it exists for");

            slider.Value = 100.0;   // what move-to-point moved the thumb to
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            });

            Assert.Equal(new[] { 100.0 }, transport.SeekCalls);
            Assert.Equal(new[] { true }, transport.PlayingCalls);
        });
    }

    [Fact]
    public void Keyboard_right_commits_plus_one_second_and_home_commits_zero()
    {
        RunOnUi(() =>
        {
            var (_, slider, transport) = BuildSeekable();

            slider.RaiseEvent(KeyDown(Key.Right, slider));
            Assert.Equal(new[] { StartPosition + 1 }, transport.SeekCalls);

            slider.RaiseEvent(KeyDown(Key.Home, slider));
            Assert.Equal(new[] { StartPosition + 1, 0.0 }, transport.SeekCalls);
        });
    }

    // ---- plumbing ------------------------------------------------------

    /// <summary>
    /// The press half of a gesture. A synthetic event has no real pointer, so
    /// <c>Track.Thumb.IsMouseOver</c> is always false and the Slider's own
    /// preview-down handler (move-to-point) marks the event handled, which would
    /// hide it from the strip. Turning move-to-point off for the raise reproduces
    /// the production thumb press, where the pointer is over the thumb and the
    /// Slider leaves the event alone.
    /// </summary>
    private static void Press(Slider slider)
    {
        bool moveToPoint = slider.IsMoveToPointEnabled;
        slider.IsMoveToPointEnabled = false;
        try
        {
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            });
        }
        finally
        {
            slider.IsMoveToPointEnabled = moveToPoint;
        }
    }

    /// <summary>
    /// A seekable strip: available with a known length, so <c>IsSeekable</c> is
    /// true and the slider is enabled. The window is never shown on screen - it is
    /// placed off to the side purely so <c>PresentationSource</c> exists, which
    /// <see cref="KeyEventArgs"/> requires.
    /// </summary>
    private static (PreviewBar Bar, Slider Slider, RecordingTransport Transport) BuildSeekable()
    {
        var transport = new RecordingTransport
        {
            IsAvailable = true,
            DurationSeconds = Duration,
            PositionSeconds = StartPosition,
        };
        var vm = new PreviewBarViewModel(transport)
        {
            IsAvailable = true,
            DurationSeconds = Duration,
            PositionSeconds = StartPosition,
        };

        var bar = new PreviewBar { DataContext = vm, ShowFrame = false };
        var window = new Window
        {
            Content = bar,
            Width = 640,
            Height = 120,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -6000,
            Top = -6000,
        };
        window.Show();
        bar.ApplyTemplate();
        bar.Measure(new Size(640, 120));
        bar.Arrange(new Rect(0, 0, 640, 120));
        bar.UpdateLayout();

        var slider = (Slider)bar.FindName("SeekBar")!;
        Assert.True(slider.IsEnabled, "the seek slider must be enabled when the strip is seekable");
        Assert.Equal(Duration, slider.Maximum);
        return (bar, slider, transport);
    }

    private static KeyEventArgs KeyDown(Key key, Visual source) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

    private static void RunOnUi(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        UiThread.Run(() =>
        {
            try
            {
                EnsureThemeResources();
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        failure?.Throw();
    }

    /// <summary>
    /// The suite's shared Application is created bare (see <see cref="UiThread"/>),
    /// so PreviewBar's StaticResource lookups would throw. Merge the real theme once.
    /// </summary>
    private static void EnsureThemeResources()
    {
        var app = Application.Current!;
        // A Window close would otherwise shut the shared Application down, which
        // would end every later test on this thread.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (app.Resources.Contains("AccentABrush")) return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/JockeyBox;component/Themes/Dark.xaml"),
        });
    }

    /// <summary>Records every call; silent on StateChanged so it cannot feed back.</summary>
    private sealed class RecordingTransport : IPreviewTransport
    {
        public List<double> SeekCalls { get; } = new();
        public List<bool> PlayingCalls { get; } = new();

        public double PositionSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsAvailable { get; set; }

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public void Seek(double seconds) => SeekCalls.Add(seconds);

        public void SetPlaying(bool playing)
        {
            PlayingCalls.Add(playing);
            IsPlaying = playing;
        }
    }
}

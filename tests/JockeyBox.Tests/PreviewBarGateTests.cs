using JockeyBox.ViewModels;

namespace JockeyBox.Tests;

/// <summary>
/// The strip's seek-bar gate, which is pure view-model state and so needs no
/// window: the bar follows the media and its length, never a status line.
///
/// <para><b>Why this exists.</b> The gate was once <c>IsAvailable &amp;&amp; !HasStatus</c>,
/// so every status string - "Buffering 100%…" included, which arrives while the
/// length and the position are already known - took the whole control off the
/// strip. The operator saw a moving clock, a total and no bar. The last two rows
/// are the regression: a status text and a known length must keep the bar.</para>
/// </summary>
public sealed class PreviewBarGateTests
{
    [Theory]
    [InlineData(false, 0d, null, false)]                   // nothing loaded: the hint, no bar
    [InlineData(false, 0d, "Preview playback failed", false)] // nothing loaded, with a message
    [InlineData(true, 0d, null, true)]                     // loaded, length unknown: empty groove
    [InlineData(true, 0d, "Resolving stream\u2026", true)]  // ... and the note beside it
    [InlineData(true, 199d, null, true)]                   // length known: the bar
    [InlineData(true, 199d, "Buffering 100%\u2026", true)] // a status must not hide it
    [InlineData(true, 199d, "Downloading\u2026", true)]
    public void ShowSeekBar_follows_the_media_not_the_status_text(
        bool available, double duration, string? status, bool expected)
    {
        var strip = new PreviewBarViewModel(new SilentTransport())
        {
            IsAvailable = available,
            DurationSeconds = duration,
            StatusText = status,
        };

        Assert.Equal(expected, strip.ShowSeekBar);
        Assert.Equal(status is not null, strip.HasStatus);
    }

    /// <summary>A transport that reports nothing, so only the strip's own state decides.</summary>
    private sealed class SilentTransport : IPreviewTransport
    {
        public double PositionSeconds => 0;

        public double DurationSeconds => 0;

        public bool IsPlaying => false;

        public bool IsAvailable => false;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public void Seek(double seconds)
        {
        }

        public void SetPlaying(bool playing)
        {
        }
    }
}

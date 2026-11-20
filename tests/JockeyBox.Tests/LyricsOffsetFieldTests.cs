using JockeyBox.Views;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// What the lyrics header's offset field accepts. No view model, no window and no
/// home: the field parses a signed millisecond count and hands it to the view
/// model, which is what the slider and the nudge buttons already write.
///
/// <para>Out-of-range values are accepted here on purpose: the store is the single
/// clamp (<c>AppSettings.MaxLyricsOffsetMs</c>), and a second clamp in the view
/// could disagree with the one that persists the value.</para>
/// </summary>
public sealed class LyricsOffsetFieldTests
{
    [Theory]
    [InlineData("244", 244)]
    [InlineData("+244", 244)]
    [InlineData("-244", -244)]
    [InlineData(" 244 ms ", 244)]
    [InlineData("+244 ms", 244)]
    [InlineData("0", 0)]
    [InlineData("20000", 20000)]   // out of range: the store clamps it
    public void ParsesSignedMilliseconds(string text, double expected)
    {
        Assert.True(LyricsPanel.TryParseOffsetMs(text, out double ms));
        Assert.Equal(expected, ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12.5")]      // the store keeps whole milliseconds
    [InlineData("ms")]
    public void RejectsWhatIsNotAnInteger(string text)
    {
        Assert.False(LyricsPanel.TryParseOffsetMs(text, out _));
    }
}

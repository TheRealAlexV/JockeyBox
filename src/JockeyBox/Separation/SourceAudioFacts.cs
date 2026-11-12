using System.IO;
using System.Linq;

namespace JockeyBox.Separation;

/// <summary>
/// The source file's own audio format, measured once per render. The separator
/// is handed the sample rate (it resamples to whatever it is told, which would
/// change nothing but the rate) and the encode step is handed the rate, channel
/// count and average bitrate, so the karaoke MP3 matches the track it came from
/// instead of the encoder's defaults.
/// </summary>
public readonly record struct SourceAudioFacts(int SampleRate, int Channels, int BitrateKbps)
{
    /// <summary>
    /// Read the facts from the file's tags/header (TagLib#), falling back to
    /// NAudio. Throws when nothing can read the file: guessing a rate here would
    /// resample the render, which is the one thing the pipeline must not do.
    /// </summary>
    public static SourceAudioFacts Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Source file not found.", path);

        try
        {
            using var file = TagLib.File.Create(path);
            var p = file.Properties;
            var kbps = p.AudioBitrate > 0
                ? p.AudioBitrate
                : EstimateKbps(path, p.Duration.TotalSeconds);
            if (p.AudioSampleRate > 0 && p.AudioChannels > 0)
                return new SourceAudioFacts(p.AudioSampleRate, p.AudioChannels, kbps);
        }
        catch
        {
            // fall through to NAudio
        }

        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(path);
            var format = reader.WaveFormat;
            return new SourceAudioFacts(
                format.SampleRate,
                format.Channels,
                EstimateKbps(path, reader.TotalTime.TotalSeconds));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot read the audio format of '{Path.GetFileName(path)}', so the " +
                "karaoke MP3 would have to be resampled: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Average bitrate in kbps from file size and duration, rounded up so the
    /// encode never undershoots the source's own bitrate.
    /// </summary>
    private static int EstimateKbps(string path, double seconds)
    {
        if (seconds <= 0) return 0;

        var bits = new FileInfo(path).Length * 8.0;
        return (int)Math.Ceiling(bits / seconds / 1000.0);
    }
}

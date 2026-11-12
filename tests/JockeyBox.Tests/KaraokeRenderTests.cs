using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JockeyBox.Downloads;
using JockeyBox.Library;
using JockeyBox.Separation;
using JockeyBox.Settings;
using Xunit;

namespace JockeyBox.Tests;

/// <summary>
/// The karaoke render pipeline's two contracts: what the separator is told to
/// produce (WAV, at the source rate, unnormalised, the mode's own stem), and what
/// the encode step does with it (one libmp3lame pass at the source's rate,
/// channels and bitrate). A single trim or filter flag anywhere in either argv
/// would break the operator's hard rule that a render has the source's duration.
/// </summary>
public sealed class KaraokeRenderTests
{
    private const int Rate = 44_100;
    private const int Channels = 2;
    private const int Kbps = 128;

    private static SeparationEngine.SeparationRequest Request(
        KaraokeStemMode mode, string output = @"C:\Music\Artist\Song.karaoke.mp3") =>
        new(@"C:\Music\Artist\Song.mp3", output, mode, Rate, Channels, Kbps);

    // ---- separation argv ---------------------------------------------------

    [Theory]
    [InlineData(KaraokeStemMode.Instrumental, "UVR-MDX-NET-Inst_HQ_3.onnx", "Instrumental")]
    [InlineData(KaraokeStemMode.InstrumentalKeepBacking, "UVR_MDXNET_KARA_2.onnx", "Instrumental")]
    [InlineData(KaraokeStemMode.VocalsOnly, "Kim_Vocal_2.onnx", "Vocals")]
    public void Separation_argv_selects_the_mode_s_model_and_stem(
        KaraokeStemMode mode, string expectedModel, string expectedStem)
    {
        var args = SeparationEngine.BuildSeparationArgs(Request(mode), @"C:\Temp\sep-1");

        Assert.Equal(expectedModel, ValueOf(args, "--model_filename"));
        Assert.Equal(expectedStem, ValueOf(args, "--single_stem"));
    }

    [Fact]
    public void Separation_argv_always_asks_for_wav_at_the_source_rate_unnormalised()
    {
        foreach (var mode in Enum.GetValues<KaraokeStemMode>())
        {
            var args = SeparationEngine.BuildSeparationArgs(Request(mode), @"C:\Temp\sep-1");

            Assert.Equal("WAV", ValueOf(args, "--output_format"));
            Assert.Equal(Rate.ToString(), ValueOf(args, "--sample_rate"));
            Assert.Equal("1.0", ValueOf(args, "--normalization"));
            Assert.Equal(@"C:\Temp\sep-1", ValueOf(args, "--output_dir"));
        }
    }

    [Fact]
    public void Separation_argv_keeps_the_input_and_model_directory()
    {
        var args = SeparationEngine.BuildSeparationArgs(Request(KaraokeStemMode.Instrumental), @"C:\Temp\sep-1");

        Assert.Equal(@"C:\Music\Artist\Song.mp3", args[0]);
        Assert.Equal(SeparationEngine.ModelsDir, ValueOf(args, "--model_file_dir"));
        Assert.Equal("info", ValueOf(args, "--log_level"));
    }

    [Fact]
    public void Separation_argv_carries_no_trim_or_filter_flag()
    {
        foreach (var mode in Enum.GetValues<KaraokeStemMode>())
        {
            var args = SeparationEngine.BuildSeparationArgs(Request(mode), @"C:\Temp\sep-1");
            AssertDoesNotShiftTime(args);
        }
    }

    // ---- encode argv -------------------------------------------------------

    [Fact]
    public void Encode_argv_matches_the_source_encoder_settings()
    {
        var request = Request(KaraokeStemMode.Instrumental);
        var args = SeparationEngine.BuildEncodeArgs(request, @"C:\Temp\sep-1\Song_(Instrumental).wav");

        Assert.Equal("libmp3lame", ValueOf(args, "-codec:a"));
        Assert.Equal("128k", ValueOf(args, "-b:a"));
        Assert.Equal("44100", ValueOf(args, "-ar"));
        Assert.Equal("2", ValueOf(args, "-ac"));
        Assert.Equal("1", ValueOf(args, "-write_xing"));
        Assert.Equal(@"C:\Music\Artist\Song.karaoke.mp3", args[^1]);
    }

    [Fact]
    public void Encode_argv_starts_from_the_separator_wav_and_strips_metadata()
    {
        var args = SeparationEngine.BuildEncodeArgs(
            Request(KaraokeStemMode.VocalsOnly, @"C:\Music\Artist\Song.vocals.mp3"),
            @"C:\Temp\sep-1\Song_(Vocals).wav");

        Assert.Equal(@"C:\Temp\sep-1\Song_(Vocals).wav", ValueOf(args, "-i"));
        Assert.Equal("-1", ValueOf(args, "-map_metadata"));
        Assert.Contains("-y", args);
        Assert.Contains("-nostdin", args);
    }

    [Fact]
    public void Encode_argv_carries_no_trim_or_filter_flag()
    {
        var args = SeparationEngine.BuildEncodeArgs(
            Request(KaraokeStemMode.Instrumental), @"C:\Temp\sep-1\Song.wav");

        AssertDoesNotShiftTime(args);
    }

    [Fact]
    public void Encode_argv_uses_the_source_rate_and_channels_not_defaults()
    {
        var request = new SeparationEngine.SeparationRequest(
            @"C:\Music\Song.flac", @"C:\Music\Song.karaoke.mp3",
            KaraokeStemMode.Instrumental, 48_000, 1, 192);

        var args = SeparationEngine.BuildEncodeArgs(request, @"C:\Temp\Song.wav");

        Assert.Equal("48000", ValueOf(args, "-ar"));
        Assert.Equal("1", ValueOf(args, "-ac"));
        Assert.Equal("192k", ValueOf(args, "-b:a"));
    }

    // ---- naming ------------------------------------------------------------

    [Fact]
    public void Instrumental_and_backing_share_the_karaoke_name_vocals_does_not()
    {
        const string source = @"C:\Music\Artist\Album\Song.mp3";

        var instrumental = KaraokeNaming.RenderPathFor(KaraokeStemMode.Instrumental, source);
        var backing = KaraokeNaming.RenderPathFor(KaraokeStemMode.InstrumentalKeepBacking, source);
        var vocals = KaraokeNaming.RenderPathFor(KaraokeStemMode.VocalsOnly, source);

        Assert.Equal(@"C:\Music\Artist\Album\Song.karaoke.mp3", instrumental);
        Assert.Equal(instrumental, backing);
        Assert.Equal(@"C:\Music\Artist\Album\Song.vocals.mp3", vocals);
        Assert.NotEqual(instrumental, vocals);
        Assert.NotEqual(backing, vocals);
    }

    [Fact]
    public void Vocals_marker_is_lowercase_and_distinct_from_the_karaoke_marker()
    {
        var path = KaraokeNaming.RenderPathFor(KaraokeStemMode.VocalsOnly, @"C:\Music\Song.mp3");

        Assert.Contains(".vocals.mp3", path);
        Assert.DoesNotContain(KaraokeNaming.Marker, path);
        Assert.False(KaraokeNaming.IsKaraokeFile(path));
    }

    [Fact]
    public void Render_name_is_stable_for_a_relative_key()
    {
        Assert.Equal(@"Artist\Album\Song.karaoke.mp3",
            KaraokeNaming.RenderName(KaraokeStemMode.Instrumental, @"Artist\Album\Song"));
        Assert.Equal(@"Artist\Album\Song.vocals.mp3",
            KaraokeNaming.RenderName(KaraokeStemMode.VocalsOnly, @"Artist\Album\Song"));
    }

    [Fact]
    public void Render_path_for_a_source_that_is_already_a_karaoke_file_stays_one_marker()
    {
        var path = KaraokeNaming.RenderPathFor(
            KaraokeStemMode.Instrumental, @"C:\Music\Song.karaoke.flac");

        Assert.Equal(@"C:\Music\Song.karaoke.mp3", path);
    }

    [Fact]
    public void Output_path_beside_the_source_agrees_with_the_naming_module()
    {
        var settings = new AppSettings { MusicDirectory = @"C:\Music" };
        const string source = @"C:\Music\Artist\Song.mp3";

        foreach (var mode in Enum.GetValues<KaraokeStemMode>())
        {
            var fromPaths = DownloadPaths.KaraokeOutputPath(settings, source, mode, @"Artist\Song");
            Assert.Equal(KaraokeNaming.RenderPathFor(mode, source), fromPaths);
        }
    }

    [Fact]
    public void Output_path_without_a_music_folder_uses_the_legacy_root_per_mode()
    {
        var settings = new AppSettings { KaraokeDirectory = @"D:\Karaoke" };

        var instrumental = DownloadPaths.KaraokeOutputPath(
            settings, null, KaraokeStemMode.Instrumental, @"Artist\Song");
        var vocals = DownloadPaths.KaraokeOutputPath(
            settings, null, KaraokeStemMode.VocalsOnly, @"Artist\Song");

        Assert.Equal(@"D:\Karaoke\Artist\Song.karaoke.mp3", instrumental);
        Assert.Equal(@"D:\Karaoke\Artist\Song.vocals.mp3", vocals);
    }

    [Fact]
    public void Output_path_is_empty_when_no_folder_can_be_decided()
    {
        Assert.Equal("", DownloadPaths.KaraokeOutputPath(
            new AppSettings(), null, KaraokeStemMode.VocalsOnly, null));
    }

    // ---- helpers -----------------------------------------------------------

    private static string ValueOf(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, $"'{flag}' missing from argv: {string.Join(' ', args)}");
        return args[i + 1];
    }

    /// <summary>None of these may appear: each one trims, pads, or time-stretches.</summary>
    private static readonly string[] TimingFlags =
    {
        "-shortest", "-ss", "-t", "-to", "-af", "-filter_complex",
    };

    private static readonly string[] TimingFilters =
    {
        "atrim", "atempo", "asetrate", "aresample", "loudnorm",
    };

    private static void AssertDoesNotShiftTime(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            Assert.DoesNotContain(arg, TimingFlags);
            foreach (var filter in TimingFilters)
                Assert.DoesNotContain(filter, arg, StringComparison.OrdinalIgnoreCase);
        }
    }
}

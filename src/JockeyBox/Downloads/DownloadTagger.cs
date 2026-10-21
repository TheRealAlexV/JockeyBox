using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JockeyBox.Sources;
using JockeyBox.Tools;

namespace JockeyBox.Downloads;

/// <summary>
/// Writes the tags a downloaded source track needs: title, artists, album,
/// track number, ISRC (as the ID3v2 "TSRC" frame) and embedded cover art.
///
/// Tagging is best-effort by design: a file that downloads but fails to tag is
/// still playable, so <see cref="ApplyAsync"/> reports failure instead of
/// throwing and the download queue carries on.
/// </summary>
public static class DownloadTagger
{
    /// <summary>Custom ID3v2 frame used for the ISRC.</summary>
    private const string IsrcFrameId = "TSRC";

    /// <summary>Cover art larger than this is rejected before touching memory.</summary>
    private const int MaxCoverBytes = 5 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// Apply tags to <paramref name="filePath"/>. Never throws: returns false when
    /// TagLib cannot open or write the file. <paramref name="coverBytes"/> may be
    /// null, in which case existing artwork is left alone.
    /// </summary>
    public static Task<bool> ApplyAsync(string filePath, SourceTrack track, byte[]? coverBytes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath) || track is null) return Task.FromResult(false);
        if (ct.IsCancellationRequested) return Task.FromResult(false);

        return Task.Run(
            () =>
            {
                try
                {
                    if (ct.IsCancellationRequested) return false;

                    using var file = TagLib.File.Create(filePath);

                    ApplyTextTags(file, filePath, track);
                    if (!string.IsNullOrEmpty(track.Isrc)) ApplyIsrc(file, track.Isrc);
                    if (coverBytes is { Length: > 0 }) ApplyCover(file, coverBytes);

                    file.Save();
                    Log.Info($"Tagged '{filePath}'");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    // A tagging failure must not fail the download.
                    Log.Error($"Could not tag '{filePath}'", ex);
                    return false;
                }
            },
            ct);
    }

    /// <summary>
    /// Download cover art for a URL. Returns null for an empty URL, a non-2xx
    /// response, or a payload larger than 5 MB; never throws.
    /// </summary>
    public static async Task<byte[]?> FetchCoverAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            using var response = await Http.GetAsync(
                url.Trim(), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Cover fetch failed ({(int)response.StatusCode}) for '{url}'");
                return null;
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared is > MaxCoverBytes)
            {
                Log.Warn($"Cover for '{url}' is {declared} bytes; skipping");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxCoverBytes)
            {
                Log.Warn($"Cover for '{url}' has an unusable length of {bytes.Length} bytes; skipping");
                return null;
            }

            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not fetch cover '{url}'", ex);
            return null;
        }
    }

    // ---- internals --------------------------------------------------------

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JockeyBox/1.0");
        return client;
    }

    private static void ApplyTextTags(TagLib.File file, string filePath, SourceTrack track)
    {
        var tag = file.Tag;
        if (tag is null) return;

        if (!string.IsNullOrEmpty(track.Title)) tag.Title = track.Title;

        var performers = SplitPerformers(track.Artist);
        if (performers.Length > 0)
        {
            // FirstPerformer is derived from Performers in TagLib#, so ordering the
            // array is how the lead artist is set.
            tag.Performers = performers;
        }

        if (!string.IsNullOrEmpty(track.Album)) tag.Album = track.Album;

        // Year is deliberately untouched: the source rarely has a better one than
        // what the file (or the rip it came from) already says.
        tag.Track = ParseTrackNumber(filePath);
    }

    private static void ApplyIsrc(TagLib.File file, string isrc)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2, create: true) is not TagLib.Id3v2.Tag id3)
        {
            Log.Debug("Tag format has no ID3v2 tag; ISRC not stored");
            return;
        }

        var existing = TagLib.Id3v2.TextInformationFrame.Get(id3, IsrcFrameId, create: true);
        if (existing is null)
        {
            Log.Debug("Could not create the TSRC frame; ISRC not stored");
            return;
        }

        existing.Text = new[] { isrc };
    }

    private static void ApplyCover(TagLib.File file, byte[] coverBytes)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2, create: true) is not TagLib.Id3v2.Tag id3)
        {
            Log.Debug("Tag format has no ID3v2 tag; cover art not stored");
            return;
        }

        // TagLib# 2.3 exposes the APIC frame as AttachmentFrame (the old
        // AttachedPictureFrame name is an obsolete alias without a Data property).
        var picture = new TagLib.Id3v2.AttachmentFrame
        {
            Type = TagLib.PictureType.FrontCover,
            MimeType = DetectMimeType(coverBytes),
            Description = "Cover",
            Data = new TagLib.ByteVector(coverBytes),
        };

        id3.AddFrame(picture);
    }

    /// <summary>Split a multi-artist string into TagLib performers.</summary>
    private static string[] SplitPerformers(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return Array.Empty<string>();

        return artist
            .Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
    }

    /// <summary>Read the zero-padded track number the layout puts in front of the title, if any.</summary>
    private static uint ParseTrackNumber(string filePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrEmpty(name)) return 0;

            var digits = new StringBuilder(4);
            foreach (var ch in name)
            {
                if (!char.IsAsciiDigit(ch)) break;
                digits.Append(ch);
            }

            if (digits.Length == 0) return 0;
            return uint.TryParse(digits.ToString(), out var number) ? number : 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not parse a track number from '{filePath}'", ex);
            return 0;
        }
    }

    /// <summary>Sniff the image type from its magic bytes; JPEG is the fallback.</summary>
    private static string DetectMimeType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";

        if (bytes.Length >= 4 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            return "image/gif";

        if (bytes.Length >= 12
            && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";

        return "image/jpeg";
    }
}

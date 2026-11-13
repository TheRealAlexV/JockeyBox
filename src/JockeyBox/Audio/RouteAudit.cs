using System;
using System.Collections.Generic;
using System.Linq;
using JockeyBox.Audio.Graph;

namespace JockeyBox.Audio;

/// <summary>One render endpoint as the router sees it: identity, state, and whether Windows calls it the default.</summary>
public readonly record struct RouteEndpoint(string Id, string Name, bool Present, bool IsDefault)
{
    /// <summary>True for a DSP/virtual driver (FxSound, VB-Audio, CABLE): the kind that can swallow audio.</summary>
    public bool IsVirtual => !Classify(Name).StartsWith("physical", StringComparison.OrdinalIgnoreCase);

    public string Kind => Classify(Name);

    /// <summary>Same classifier the probe and the log use, so the vocabulary cannot drift.</summary>
    public static string Classify(string name) => AudioProbe.Classify(name);
}

/// <summary>One line the router wants written, and whether it is a warning.</summary>
public readonly record struct RouteNotice(bool IsWarning, string Message);

/// <summary>The route after fallbacks, plus the lines to log.</summary>
public sealed record RouteCheck(AudioRoute Route, IReadOnlyList<RouteNotice> Notices)
{
    public bool AnyWarning => Notices.Any(n => n.IsWarning);
}

/// <summary>
/// Decides, from endpoint facts alone, whether a configured route can be played as
/// stored. Pure: no device access, so the cases that matter can be tested without
/// an audio stack.
///
/// <para><b>Why.</b> The app could play confidently into a virtual DSP endpoint that
/// was not the Windows default, show healthy peaks at every sink, and produce
/// silence with no hint anywhere. Both facts are knowable at route time: whether
/// the stored device still exists, and whether it is the system default. This
/// class turns them into log lines and, when the device is gone, into a
/// substitution.</para>
/// </summary>
public static class RouteAudit
{
    /// <summary>The bus label used in every route log line, matching the audio trace.</summary>
    public static string Label(BusKind kind) => kind switch
    {
        BusKind.DeckA => "deckA",
        BusKind.DeckB => "deckB",
        BusKind.Recording => "recording",
        BusKind.Soundboard => "soundboard",
        BusKind.Monitor => "monitor",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Fall back to the default endpoint for any configured id that is missing or
    /// not present, then describe - in one line - every output that is not the
    /// Windows default.
    /// </summary>
    public static RouteCheck Check(AudioRoute route, IReadOnlyList<RouteEndpoint> endpoints, string? defaultId)
    {
        var notices = new List<RouteNotice>();
        var resolved = route;
        var defaultEndpoint = defaultId == null
            ? (RouteEndpoint?)null
            : endpoints.FirstOrDefault(e => Same(e.Id, defaultId));

        // 1. Substitution: a stored id whose device is gone would otherwise land on
        //    the default silently (DeviceService does that), leaving the operator's
        //    setting looking honoured when it was not.
        foreach (var kind in AllBuses)
        {
            string? id = route.DeviceFor(kind);
            if (string.IsNullOrWhiteSpace(id)) continue;

            var match = endpoints.FirstOrDefault(e => Same(e.Id, id));
            bool found = !string.IsNullOrEmpty(match.Id);
            if (found && match.Present) continue;

            string why = found ? $"is {match.Name} but not present" : "is not on this machine";
            string? replacement = defaultEndpoint?.Id;
            if (string.IsNullOrWhiteSpace(replacement))
            {
                notices.Add(new RouteNotice(true,
                    $"audio route: {Label(kind)} fell back to default failed: the configured device {why} " +
                    "and Windows reports no default render endpoint"));
                continue;
            }

            resolved = WithDevice(resolved, kind, replacement);
            notices.Add(new RouteNotice(true,
                $"audio route: {Label(kind)} fell back to default '{defaultEndpoint!.Value.Name}' " +
                $"(configured device {why})"));
        }

        // 2. One mismatch line for the whole route, at most: per-bus or per-block
        //    repeats are noise, and the combination that actually goes silent is a
        //    virtual endpoint that is not the default, so that combination warns.
        var mismatched = new List<(string Labels, RouteEndpoint Endpoint)>();
        foreach (var kind in AllBuses)
        {
            string? id = resolved.DeviceFor(kind);
            if (string.IsNullOrWhiteSpace(id)) continue;
            var match = endpoints.FirstOrDefault(e => Same(e.Id, id));
            if (string.IsNullOrEmpty(match.Id)) continue;      // already reported above
            if (match.IsDefault) continue;

            int existing = mismatched.FindIndex(m => Same(m.Endpoint.Id, match.Id));
            string label = Label(kind);
            if (existing >= 0)
                mismatched[existing] = (mismatched[existing].Labels + "+" + label, mismatched[existing].Endpoint);
            else
                mismatched.Add((label, match));
        }

        if (mismatched.Count > 0)
        {
            string defaultName = defaultEndpoint?.Name ?? "none";
            string list = string.Join("; ", mismatched.Select(m => $"{m.Labels} on '{m.Endpoint.Name}' [{m.Endpoint.Kind}]"));
            bool anyVirtual = mismatched.Any(m => m.Endpoint.IsVirtual);
            string subject = mismatched.Count == 1
                ? $"{list} is"
                : $"{mismatched.Count} outputs are";
            notices.Add(new RouteNotice(anyVirtual,
                $"audio route: {subject} not the Windows default '{defaultName}'{(mismatched.Count == 1 ? "" : ": " + list)}"));
        }

        return new RouteCheck(resolved, notices);
    }

    private static readonly BusKind[] AllBuses =
    {
        BusKind.DeckA, BusKind.DeckB, BusKind.Recording, BusKind.Soundboard, BusKind.Monitor,
    };

    private static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static AudioRoute WithDevice(AudioRoute route, BusKind kind, string id) => kind switch
    {
        BusKind.DeckA => route with { DeckADeviceId = id },
        BusKind.DeckB => route with { DeckBDeviceId = id },
        BusKind.Recording => route with { RecordingDeviceId = id },
        BusKind.Soundboard => route with { SoundboardDeviceId = id },
        BusKind.Monitor => route with { MonitorDeviceId = id },
        _ => route,
    };
}

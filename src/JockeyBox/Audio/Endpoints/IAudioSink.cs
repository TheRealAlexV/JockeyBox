using System;

namespace JockeyBox.Audio.Endpoints;

/// <summary>
/// One place a rendered block can go: a physical device (<see cref="EndpointSink"/>)
/// or the virtual ASIO transport ring (<see cref="VirtualBusSink"/>).
///
/// <para><b>Why this exists.</b> The engine used to test "is there any output at
/// all?" against a <see cref="EndpointSink"/> field. A bus routed only to the
/// virtual driver has no WASAPI session, so the engine started, found no device,
/// and quit - and the operator heard silence with nothing in the log. The seam is
/// the shape of the question, not the device: any sink that can take a block keeps
/// the engine running.</para>
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>Friendly name for diagnostics and the route log.</summary>
    string DeviceName { get; }

    /// <summary>
    /// True once the sink can no longer take blocks (a device failed, the ring
    /// was lost). The engine drops it instead of writing into it.
    /// </summary>
    bool HasFailed { get; }

    /// <summary>
    /// Why <see cref="HasFailed"/> is true; null while healthy. Set once, on first
    /// detection, so the log names the cause instead of the bare fact of failure.
    /// </summary>
    string? FailureReason { get; }

    /// <summary>Append one rendered block. Never throws: a failure is logged once and the sink goes quiet.</summary>
    void Write(float[] interleaved, int offset, int frames);
}

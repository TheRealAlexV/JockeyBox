namespace JockeyBox.Ipc;

/// <summary>Outcome of a <see cref="VirtualBusReader.Read"/> call.</summary>
public enum VirtualBusStatus
{
    /// <summary>Fresh block copied bit-exactly into the destination.</summary>
    Data = 0,

    /// <summary>No new block; the destination was cleared. Counted as an underrun.</summary>
    Silence = 1,

    /// <summary>
    /// The reader fell too far behind (overrun by the writer, or beyond its configured
    /// maximum lag) and jumped to the newest block. The destination was cleared; nothing
    /// stale was replayed. Counted as a correction.
    /// </summary>
    Correction = 2,
}

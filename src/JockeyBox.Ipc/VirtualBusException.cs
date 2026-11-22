namespace JockeyBox.Ipc;

/// <summary>
/// Thrown when a shared bus region cannot be used: wrong magic, mismatched version,
/// a second writer, missing writer, or an inconsistent geometry.
/// Messages always name the expected and the found value.
/// </summary>
public sealed class VirtualBusException : Exception
{
    public VirtualBusException(string message) : base(message)
    {
    }

    public VirtualBusException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

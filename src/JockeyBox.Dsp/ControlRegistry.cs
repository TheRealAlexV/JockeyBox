namespace JockeyBox.Dsp;

/// <summary>
/// Registry of <see cref="ControlDescriptor"/>s, keyed by id. Ids must be unique; a duplicate
/// registration throws with a message that names the id.
/// </summary>
public sealed class ControlRegistry
{
    private readonly Dictionary<string, ControlDescriptor> _byId = new(StringComparer.Ordinal);
    private readonly List<ControlDescriptor> _all = new();

    /// <summary>Every registered control, in registration order.</summary>
    public IReadOnlyList<ControlDescriptor> All => _all;

    public void Register(ControlDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);

        if (_byId.TryGetValue(descriptor.Id, out ControlDescriptor? existing))
        {
            throw new ArgumentException(
                $"A control with id '{descriptor.Id}' is already registered as '{existing.Label}'; control ids must be unique.",
                nameof(descriptor));
        }

        _byId.Add(descriptor.Id, descriptor);
        _all.Add(descriptor);
    }

    public void RegisterRange(IEnumerable<ControlDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        foreach (ControlDescriptor descriptor in descriptors)
        {
            Register(descriptor);
        }
    }

    public bool TryGet(string id, out ControlDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _byId.TryGetValue(id, out descriptor);
    }
}

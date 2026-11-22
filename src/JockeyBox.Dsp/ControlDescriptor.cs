namespace JockeyBox.Dsp;

/// <summary>Where a control applies.</summary>
public enum ControlScope
{
    Global,
    PerChannel,
    PerBus,
}

/// <summary>How a control is presented and edited.</summary>
public enum ControlKind
{
    Continuous,
    Toggle,
    Choice,
}

/// <summary>
/// Metadata for one user-facing control. <see cref="Min"/>, <see cref="Max"/>,
/// <see cref="Step"/> and <see cref="Unit"/> describe a <see cref="ControlKind.Continuous"/>
/// control; <see cref="Choices"/> lists the options for a <see cref="ControlKind.Choice"/>.
/// </summary>
public sealed record ControlDescriptor(
    string Id,
    string Label,
    string Group,
    ControlScope Scope,
    ControlKind Kind,
    double Min = 0,
    double Max = 1,
    double Step = 0.01,
    double Default = 0,
    string Unit = "",
    IReadOnlyList<string>? Choices = null);

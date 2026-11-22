namespace JockeyBox.Dsp;

/// <summary>
/// Linear parameter ramp: moves from the current value to a target over a fixed number of
/// frames so a knob move cannot produce zipper noise. Allocation-free.
/// </summary>
internal struct Smoother
{
    public double Value;
    public double Target;
    public double Step;
    public int Remaining;

    public Smoother(double value)
    {
        Value = value;
        Target = value;
        Step = 0;
        Remaining = 0;
    }

    public void SetTarget(double target, int frames)
    {
        Target = target;
        if (frames <= 0 || target == Value)
        {
            Value = target;
            Step = 0;
            Remaining = 0;
            return;
        }

        Step = (target - Value) / frames;
        Remaining = frames;
    }

    public double Next()
    {
        if (Remaining <= 0) return Value;
        Value += Step;
        Remaining--;
        if (Remaining == 0) Value = Target;
        return Value;
    }

    public void Snap()
    {
        Value = Target;
        Step = 0;
        Remaining = 0;
    }
}

using System;
using System.Globalization;
using System.Windows.Data;

namespace JockeyBox.Views;

/// <summary>
/// Elapsed time for one <c>ActivityRow</c>, as "m:ss" (or "h:mm:ss" past an
/// hour). Bound as a MultiBinding over Started, Finished and Percent so the
/// clock recomputes on every progress tick while the job is running; a finished
/// row freezes at its own <c>Finished</c> stamp. <c>ActivityRow</c> has no
/// elapsed property of its own and this console does not get to add one, so the
/// drawing layer does the arithmetic.
/// </summary>
public sealed class ActivityElapsedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        // values[2] (Percent) is a tick source only; it is deliberately unused.
        if (values.Length < 2 || values[0] is not DateTimeOffset started) return "";

        var finished = values[1] as DateTimeOffset?;
        var span = (finished ?? DateTimeOffset.Now) - started;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

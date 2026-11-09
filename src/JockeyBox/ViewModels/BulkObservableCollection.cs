using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace JockeyBox.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be filled in one go.
///
/// <para>A bound WPF list reacts to every <see cref="ObservableCollection{T}.Add"/>
/// with its own notification: a 5 000-row playlist built row by row is 5 000
/// measure/arrange passes. <see cref="ReplaceAll"/> swaps the backing items and
/// raises exactly one <see cref="NotifyCollectionChangedAction.Reset"/>, so the
/// grid lays out once.</para>
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    /// <summary>Replace the contents, notifying listeners once with a Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Append a page of items with a single Reset (used while streaming).</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        if (!added) return;

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MusicPlayer.Services;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can swap its whole contents
/// with a single change notification.
/// </summary>
/// <remarks>
/// Refilling the base class costs one Reset (from Clear) plus one Insert per
/// item. For a few thousand tracks that is thousands of separate collection
/// change events for every search keystroke or sort change, each one giving the
/// bound ItemsControl a chance to invalidate layout. <see cref="ReplaceAll"/>
/// raises exactly one Reset instead.
/// </remarks>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChangedArgs = new("Count");
    private static readonly PropertyChangedEventArgs IndexerChangedArgs = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetChangedArgs =
        new(NotifyCollectionChangedAction.Reset);

    /// <summary>
    /// Replace every element in one shot, raising a single Reset notification.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        if (items != null)
        {
            foreach (var item in items)
                Items.Add(item);
        }

        OnPropertyChanged(CountChangedArgs);
        OnPropertyChanged(IndexerChangedArgs);
        OnCollectionChanged(ResetChangedArgs);
    }
}

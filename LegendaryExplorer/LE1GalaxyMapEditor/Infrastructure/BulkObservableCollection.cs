using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LE1GalaxyMapEditor.Infrastructure;

/// <summary>
/// Replaces a complete UI list with a single Reset notification. This avoids
/// asking WPF to re-layout the same control once per diagnostic or module.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using Packo.Models;
using System.Collections.ObjectModel;

namespace Packo.Helpers;

public static class PackingItemOrder
{
    // Identity breaks ties for a batch created at the same instant.
    public static IOrderedEnumerable<PackingItem> Sort(IEnumerable<PackingItem> items) => items
        .OrderBy(item => item.IsPacked)
        .ThenBy(item => item.CreatedDate.ToUniversalTime())
        .ThenBy(item => item.RemoteId is > 0 ? item.RemoteId.Value : int.MaxValue)
        .ThenBy(item => item.LocalId);

    public static void Apply(ObservableCollection<PackingItem> items, PackingItem? changedItem = null)
    {
        var ordered = Sort(items).ToList();
        // A checkbox change needs just one move in either direction.
        if (changedItem != null && items.Contains(changedItem))
        {
            var from = items.IndexOf(changedItem);
            var to = ordered.IndexOf(changedItem);
            if (from != to) items.Move(from, to);
        }
        for (var i = ordered.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(items[i], ordered[i])) items.Move(items.IndexOf(ordered[i]), i);
    }
}

using Packo.Models;
using System.Collections.ObjectModel;

namespace Packo.Helpers;

public enum PackingItemSortMode
{
    UnpackedFirst,
    PackedFirst,
    NameAscending,
    NameDescending,
    QuantityDescending,
    QuantityAscending
}

public static class PackingItemOrder
{
    public static IOrderedEnumerable<PackingItem> Sort(IEnumerable<PackingItem> items,
        PackingItemSortMode mode = PackingItemSortMode.UnpackedFirst)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        var ordered = mode switch
        {
            PackingItemSortMode.PackedFirst => items.OrderByDescending(item => item.IsPacked),
            PackingItemSortMode.NameAscending => items.OrderBy(item => item.Name, byName),
            PackingItemSortMode.NameDescending => items.OrderByDescending(item => item.Name, byName),
            PackingItemSortMode.QuantityDescending => items.OrderByDescending(item => item.Quantity)
                .ThenBy(item => item.Name, byName),
            PackingItemSortMode.QuantityAscending => items.OrderBy(item => item.Quantity)
                .ThenBy(item => item.Name, byName),
            _ => items.OrderBy(item => item.IsPacked)
        };

        // Identity breaks ties for a batch created at the same instant.
        return ordered.ThenBy(item => item.CreatedDate.ToUniversalTime())
            .ThenBy(item => item.RemoteId is > 0 ? item.RemoteId.Value : int.MaxValue)
            .ThenBy(item => item.LocalId);
    }

    public static void Apply(ObservableCollection<PackingItem> items, PackingItem? changedItem = null,
        PackingItemSortMode mode = PackingItemSortMode.UnpackedFirst)
    {
        var ordered = Sort(items, mode).ToList();
        // A single changed item usually needs just one move.
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

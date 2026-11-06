using Packo.Models.DTO;

namespace Packo.Helpers;

public static class PackingListCopy
{
    public static List<PackingSuggestion> ExcludeExisting(IEnumerable<PackingSuggestion> candidates,
        IEnumerable<string> existingNames)
    {
        var names = new HashSet<string>(existingNames.Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);
        return candidates.Where(item => names.Add(item.Name.Trim())).ToList();
    }
}

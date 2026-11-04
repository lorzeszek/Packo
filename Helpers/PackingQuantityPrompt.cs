using CommunityToolkit.Maui.Extensions;
using Packo.Popups;

namespace Packo.Helpers;

public static class PackingQuantityPrompt
{
    public static async Task<int?> ShowAsync(string name, int quantity)
    {
        var result = await Shell.Current.ShowPopupAsync<int>(new PackingQuantityPopup(name, quantity));
        return result.Result > 0 ? result.Result : null;
    }
}

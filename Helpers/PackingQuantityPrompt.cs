using System.Globalization;
using Packo.Services;

namespace Packo.Helpers;

public static class PackingQuantityPrompt
{
    public static async Task<int?> ShowAsync(string name, int quantity)
    {
        var resources = LocalizationResourceManager.Instance;
        while (true)
        {
            var input = await Shell.Current.DisplayPromptAsync(resources["PackingQuantity"], name,
                resources["SaveChanges"], resources["Cancel"], keyboard: Keyboard.Numeric,
                initialValue: quantity.ToString(CultureInfo.CurrentCulture));
            if (input is null) return null;
            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value >= 1)
                return value;
            await Shell.Current.DisplayAlertAsync(resources["PackingQuantity"], resources["PackingQuantityInvalid"], resources["Ok"]);
        }
    }
}

using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Models.DTO;

namespace Packo.Popups;

public partial class PackingListNamePopup : Popup<string?>
{
    private PackingListNamePopup(string title, string initialValue, double width)
    {
        InitializeComponent();
        PopupCard.WidthRequest = width;
        TitleLabel.Text = title;
        NameEntry.MaxLength = PackingListDTO.MaxNameLength;
        NameEntry.Text = initialValue;
        NameEntry.TextChanged += (_, _) => SaveButton.IsEnabled = ValidName is not null;
        SaveButton.IsEnabled = ValidName is not null;
        Opened += (_, _) => Dispatcher.Dispatch(() => NameEntry.Focus());
    }

    private string? ValidName => NameEntry.Text?.Trim() is { Length: > 0 and <= PackingListDTO.MaxNameLength } name
        ? name
        : null;

    public static async Task<string?> ShowAsync(string title, string initialValue = "")
    {
        var page = Application.Current!.MainPage!;
        var popup = new PackingListNamePopup(title, initialValue, PopupCardSizing.GetWidth(page));
        var result = await page.ShowPopupAsync<string>(popup);
        return result.Result;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (ValidName is { } name)
            await CompleteAsync(name);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CompleteAsync(null);

    private async Task CompleteAsync(string? name)
    {
        if (!IsEnabled)
            return;

        IsEnabled = false;
        NameEntry.Unfocus();
        await CloseAsync(name);
    }
}

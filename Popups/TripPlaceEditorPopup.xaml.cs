using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Models;

namespace Packo.Popups;

public partial class TripPlaceEditorPopup : Popup<TripPlace>
{
    private TripPlaceEditorPopup(Page page, string destination)
    {
        InitializeComponent();
        PopupCard.WidthRequest = PopupCardSizing.GetWidth(page);
        PopupCard.MaximumHeightRequest = Math.Max(280, page.Height * 0.85);
        AddressEntry.Text = destination;
    }

    public static async Task<TripPlace?> ShowAsync(string destination)
    {
        var page = Shell.Current.CurrentPage;
        var result = await page.ShowPopupAsync<TripPlace>(new TripPlaceEditorPopup(page, destination));
        return result.Result;
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (SaveButton is null || WebsiteEntry is null) return;
        var website = WebsiteEntry.Text?.Trim();
        var validUrl = string.IsNullOrWhiteSpace(website) || TripPlace.WebUri(website) is not null;
        UrlError.IsVisible = !validUrl;
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameEntry.Text) && validUrl;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!IsEnabled || !SaveButton.IsEnabled) return;
        IsEnabled = false;
        await CloseAsync(new TripPlace
        {
            Id = "manual:" + Guid.NewGuid().ToString("N"), Name = NameEntry.Text.Trim(),
            LocationText = AddressEntry.Text?.Trim() ?? string.Empty, Description = NoteEditor.Text?.Trim() ?? string.Empty,
            WebsiteUrl = TripPlace.WebUri(WebsiteEntry.Text?.Trim())?.AbsoluteUri, Category = "manual"
        });
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        await CloseAsync(null!);
    }
}

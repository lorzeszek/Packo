using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Models;
using Packo.Services;

namespace Packo.Popups;

public partial class TripPlaceEditorPopup : Popup<TripPlace>
{
    private readonly TripPlace? _place;
    private TripPlaceEditorPopup(Page page, string destination, TripPlace? place = null)
    {
        _place = place;
        InitializeComponent();
        PopupCard.WidthRequest = PopupCardSizing.GetWidth(page);
        PopupCard.MaximumHeightRequest = Math.Max(280, page.Height * 0.85);
        AddressEntry.Text = destination;
        if (place?.IsGoogle == true)
        {
            TitleLabel.Text = LocalizationResourceManager.Instance["PlacesMyNote"];
            NameCaption.Text = LocalizationResourceManager.Instance["PlacesOwnLabel"];
            NameEntry.Placeholder = LocalizationResourceManager.Instance["PlacesOwnLabel"];
            NameEntry.Text = place.UserLabel;
            NoteEditor.Text = place.UserNote;
            AddressCaption.IsVisible = AddressEntry.IsVisible = WebsiteCaption.IsVisible = WebsiteEntry.IsVisible = false;
            SaveButton.IsEnabled = true;
        }
        else if (place is not null)
        {
            TitleLabel.Text = LocalizationResourceManager.Instance["PlacesEditPlace"];
            NameEntry.Text = place.Name;
            AddressEntry.Text = place.LocationText;
            NoteEditor.Text = string.IsNullOrWhiteSpace(place.UserNote) ? place.Description : place.UserNote;
            WebsiteEntry.Text = place.WebsiteUrl;
        }
    }

    public static async Task<TripPlace?> EditAsync(TripPlace place)
    {
        if (!place.IsGoogle && !place.IsManual) return null;
        var page = Shell.Current.CurrentPage;
        var result = await page.ShowPopupAsync<TripPlace>(new TripPlaceEditorPopup(page, "", place));
        return result.Result;
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
        SaveButton.IsEnabled = (_place?.IsGoogle == true || !string.IsNullOrWhiteSpace(NameEntry.Text)) && validUrl;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!IsEnabled || !SaveButton.IsEnabled) return;
        IsEnabled = false;
        if (_place?.IsGoogle == true)
        {
            await CloseAsync(_place with { UserLabel = NameEntry.Text?.Trim() ?? "", UserNote = NoteEditor.Text?.Trim() ?? "" });
            return;
        }
        var place = _place ?? new TripPlace { Id = "manual:" + Guid.NewGuid().ToString("N"), Category = "manual" };
        await CloseAsync(place with
        {
            Name = NameEntry.Text.Trim(), UserLabel = string.Empty, UserNote = string.Empty,
            LocationText = AddressEntry.Text?.Trim() ?? string.Empty, Description = NoteEditor.Text?.Trim() ?? string.Empty,
            WebsiteUrl = TripPlace.WebUri(WebsiteEntry.Text?.Trim())?.AbsoluteUri
        });
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (!IsEnabled) return;
        IsEnabled = false;
        await CloseAsync(null!);
    }
}

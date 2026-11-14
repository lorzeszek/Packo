using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Services;

namespace Packo.Popups;

public partial class ReminderNotificationPopup : Popup<bool>
{
    private bool _closing;

    private ReminderNotificationPopup(Page page, string tripName, string? title, string? body)
    {
        InitializeComponent();
        PopupCard.WidthRequest = PopupCardSizing.GetWidth(page);
        var height = page.Height > 0 ? page.Height : 740;
        PopupCard.MaximumHeightRequest = Math.Max(240, height - 70);
        MessageScroll.MaximumHeightRequest = Math.Clamp(height - 310, 120, 360);
        TripNameLabel.Text = tripName;
        ReminderTitleLabel.Text = title;
        ReminderTitleLabel.IsVisible = !string.IsNullOrWhiteSpace(title);
        MessageLabel.Text = string.IsNullOrWhiteSpace(body)
            ? LocalizationResourceManager.Instance["ReminderNotificationTextUnavailable"] : body;
    }

    public static async Task<bool> ShowAsync(Page page, string tripName, string? title, string? body) =>
        (await page.ShowPopupAsync<bool>(new ReminderNotificationPopup(page, tripName, title, body))).Result;

    private async void OnCloseClicked(object? sender, EventArgs e) => await FinishAsync(false);
    private async void OnDetailsClicked(object? sender, EventArgs e) => await FinishAsync(true);
    private async Task FinishAsync(bool openDetails)
    {
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        await CloseAsync(openDetails);
    }
}

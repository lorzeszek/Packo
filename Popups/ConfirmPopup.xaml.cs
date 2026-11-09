using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Services;

namespace Packo.Popups;

public partial class ConfirmPopup : Popup<bool>
{
    private ConfirmPopup(string title, string? message, string confirmText, string? cancelText)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        MessageLabel.IsVisible = !string.IsNullOrWhiteSpace(message);
        ConfirmButton.Text = confirmText;
        CancelButton.Text = cancelText;
        CancelButton.IsVisible = cancelText is not null;
        if (cancelText is null)
        {
            Grid.SetColumn(ConfirmButton, 0);
            Grid.SetColumnSpan(ConfirmButton, 2);
        }
    }

    public static async Task<bool> AskAsync(string title, string? message = null,
        string? confirmText = null, string? cancelText = null)
    {
        var strings = LocalizationResourceManager.Instance;
        var popup = new ConfirmPopup(title, message, confirmText ?? strings["Yes"], cancelText ?? strings["No"]);
        var result = await Application.Current!.MainPage!.ShowPopupAsync<bool>(popup);
        return result.Result;
    }

    public static async Task ShowMessageAsync(string title, string message)
    {
        var popup = new ConfirmPopup(title, message, LocalizationResourceManager.Instance["Ok"], null);
        await Application.Current!.MainPage!.ShowPopupAsync<bool>(popup);
    }

    private async void OnConfirmClicked(object? sender, EventArgs e) => await CompleteAsync(true);

    private async void OnCancelClicked(object? sender, EventArgs e) => await CompleteAsync(false);

    private async Task CompleteAsync(bool result)
    {
        if (!IsEnabled)
            return;

        IsEnabled = false;
        await CloseAsync(result);
    }
}

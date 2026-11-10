using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;

namespace Packo.Popups;

public partial class OptionsPopup : Popup<string?>
{
    private OptionsPopup(string title, IReadOnlyList<string> options)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        BindableLayout.SetItemsSource(OptionsList, options);
        if (options.Count > 2)
        {
            OptionsHost.Content = null;
            OptionsHost.Content = new ScrollView { MaximumHeightRequest = 264, Content = OptionsList };
        }
    }

    public static async Task<string?> ChooseAsync(string title, IReadOnlyList<string> options)
    {
        var result = await Application.Current!.MainPage!.ShowPopupAsync<string>(new OptionsPopup(title, options));
        return result.Result;
    }

    private async void OnOptionClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: string option })
            await CompleteAsync(option);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CompleteAsync(null);

    private async Task CompleteAsync(string? result)
    {
        if (!IsEnabled)
            return;

        IsEnabled = false;
        await CloseAsync(result);
    }
}

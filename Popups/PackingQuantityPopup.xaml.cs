using CommunityToolkit.Maui.Views;
using System.Globalization;

namespace Packo.Popups;

public partial class PackingQuantityPopup : Popup<int>
{
    private bool _isInitialized;
    private bool _isClosing;

    public PackingQuantityPopup(string name, int quantity)
    {
        InitializeComponent();
        ItemNameLabel.Text = name;
        _isInitialized = true;
        QuantityEntry.Text = Math.Max(1, quantity).ToString(CultureInfo.CurrentCulture);
        RefreshValidation();
    }

    private bool TryGetQuantity(out int quantity) =>
        int.TryParse(QuantityEntry.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out quantity)
        && quantity >= 1;

    private void OnQuantityTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isInitialized)
            RefreshValidation();
    }

    private void RefreshValidation()
    {
        var valid = TryGetQuantity(out var quantity);
        ValidationLabel.IsVisible = !valid;
        ConfirmButton.IsEnabled = valid;
        DecreaseButton.IsEnabled = valid && quantity > 1;
        IncreaseButton.IsEnabled = valid && quantity < int.MaxValue;
        DecreaseButton.Opacity = DecreaseButton.IsEnabled ? 1 : 0.35;
        IncreaseButton.Opacity = IncreaseButton.IsEnabled ? 1 : 0.35;
    }

    private void OnDecreaseClicked(object? sender, EventArgs e) => AdjustQuantity(-1);

    private void OnIncreaseClicked(object? sender, EventArgs e) => AdjustQuantity(1);

    private void AdjustQuantity(int adjustment)
    {
        if (_isClosing || !TryGetQuantity(out var quantity))
            return;

        var adjusted = (long)quantity + adjustment;
        if (adjusted is >= 1 and <= int.MaxValue)
            QuantityEntry.Text = adjusted.ToString(CultureInfo.CurrentCulture);
    }

    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        if (_isClosing || !TryGetQuantity(out var quantity))
            return;

        await CompleteAsync(quantity);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CompleteAsync(0);

    private async Task CompleteAsync(int result)
    {
        if (_isClosing)
            return;

        _isClosing = true;
        EditorContent.IsEnabled = false;
        QuantityEntry.Unfocus();
        await CloseAsync(result);
    }
}

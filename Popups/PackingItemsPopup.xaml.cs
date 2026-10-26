using CommunityToolkit.Maui.Views;
using Packo.Popups.Objects;
using Packo.Models.DTO;
using Packo.Helpers;
using System.Collections.ObjectModel;

namespace Packo.Popups;

public partial class PackingItemsPopup : Popup
{
    public ObservableCollection<SelectableItem> Items { get; set; }

    public TaskCompletionSource<List<PackingSuggestion>> ResultSource { get; } = new();

    public PackingItemsPopup(List<PackingSuggestion> items)
    {
        InitializeComponent();

        Items = new ObservableCollection<SelectableItem>(
            items.Select(x => new SelectableItem
            {
                Name = x.Name,
                Quantity = x.Quantity,
                IsSelected = true
            }));

        BindingContext = this;
    }

    private void OnSelectAllClicked(object sender, EventArgs e)
    {
        foreach (var item in Items)
            item.IsSelected = true;
    }

    private void OnDeselectAllClicked(object sender, EventArgs e)
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    private async void OnConfirmClicked(object sender, EventArgs e)
    {
        var selected = Items
            .Where(x => x.IsSelected)
            .Select(x => new PackingSuggestion { Name = x.Name, Quantity = x.Quantity })
            .ToList();

        await CloseAsync();
        ResultSource.TrySetResult(selected);
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        await CloseAsync();
        ResultSource.TrySetResult([]);
    }

    private bool _isEditingQuantity;
    private async void OnQuantityClicked(object? sender, EventArgs e)
    {
        if (_isEditingQuantity || sender is not Button { BindingContext: SelectableItem item }) return;
        _isEditingQuantity = true;
        try
        {
            var quantity = await PackingQuantityPrompt.ShowAsync(item.Name, item.Quantity);
            if (quantity.HasValue) item.Quantity = quantity.Value;
        }
        finally { _isEditingQuantity = false; }
    }
}

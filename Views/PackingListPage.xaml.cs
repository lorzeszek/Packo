using Packo.Models.Pages;
using Packo.ViewModels;

namespace Packo.Views;

public partial class PackingListPage : BasePage//ContentPage//, IQueryAttributable
{
    private readonly PackingListViewModel _viewModel;

    public PackingListPage(PackingListViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    private void OnPackedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (sender is not CheckBox { BindingContext: Packo.Models.PackingItem item } checkBox
            || item.IsPacked == e.Value) return; // Ignore binding/realtime updates and recycled rows.
        if (!_viewModel.CanEditItems)
        {
            checkBox.IsChecked = item.IsPacked;
            return;
        }
        item.IsPacked = e.Value;
        _viewModel.ToggleIsPackedCommand.Execute(item);
    }

    //public async void ApplyQueryAttributes(IDictionary<string, object> query)
    //{
    //    if (query.TryGetValue("tripId", out var tripIdObj) && tripIdObj is string tripId)
    //    {
    //        await _viewModel.InitializeRealtimeAsync(tripId);
    //    }
    //}

    //public void ApplyQueryAttributes(IDictionary<string, object> query)
    //{
    //    if (query.TryGetValue("tripId", out var tripIdObj) && tripIdObj is string tripId)
    //    {
    //        _ = _viewModel.InitializeRealtimeAsync(tripId);
    //    }
    //}

    //protected override async void OnAppearing()
    //{
    //    base.OnAppearing();
    //    await _viewModel.InitializeRealtimeAsync();
    //}

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        if (Shell.Current.Navigation.NavigationStack.Count > 1)
        {
            await Shell.Current.GoToAsync("..");
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _viewModel.DisposeRealtimeAsync();
    }

    //private void OnNewItemCompleted(object sender, EventArgs e)
    //{
    //    if (BindingContext is PackingListViewModel vm)
    //    {
    //        vm.AddItemCommand.
    //        vm.AddItemCommand();
    //        vm.NewItemEntry = string.Empty;
    //    }
    //}

    //private void OnNewItemCompleted(object sender, EventArgs e)
    //{
    //    if (sender is Entry entry)
    //    {
    //        var text = entry.Text?.Trim();
    //        if (!string.IsNullOrEmpty(text))
    //        {
    //            // zakładam że BindingContext = Twój ViewModel
    //            if (BindingContext is PackingListViewModel vm)
    //            {
    //                vm.Items.Add(new PackingItem
    //                {
    //                    Name = text,
    //                    IsPacked = false
    //                });
    //            }

    //            // wyczyść pole
    //            entry.Text = string.Empty;
    //        }
    //    }
    //}
}

using Packo.Models.DTO;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Services;
using System.Globalization;

namespace Packo.Views;

public sealed record PackingListCard(PackingListDTO List)
{
    public string DisplayName => List.Name ?? LocalizationResourceManager.Instance["PackingListTitle"];
    public string ProgressPercent => List.Progress.ToString("P0", CultureInfo.CurrentCulture);
    public string ProgressText => List.Total == 0
        ? LocalizationResourceManager.Instance["StartPackingEmpty"]
        : string.Format(CultureInfo.CurrentCulture, LocalizationResourceManager.Instance["StartPackingCount"],
            List.Packed, List.Total);
}

public partial class PackingListsView : ContentView
{
    private TripDTO? _trip;
    private bool _working;
    private bool _refreshing;

    public IPackingListRepository Repository { private get; set; } = null!;

    public PackingListsView() => InitializeComponent();

    public async Task ShowAsync(TripDTO? trip)
    {
        if (_working || _refreshing || trip is null)
            return;
        _trip = trip;
        _refreshing = true;
        try
        {
            await LoadAsync(trip, refresh: false);
            LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = true;
            var refreshed = await Repository.GetPackingListsAsync(trip);
            if (!_working)
                DisplayLists(refreshed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            _refreshing = false;
            LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = false;
        }
    }

    private async Task LoadAsync(TripDTO trip, bool refresh) =>
        DisplayLists(await Repository.GetPackingListsAsync(trip, refresh));

    private void DisplayLists(IReadOnlyList<PackingListDTO> lists)
    {
        BindableLayout.SetItemsSource(ListCards, lists.Select(list => new PackingListCard(list)).ToList());
        EmptyLabel.IsVisible = lists.Count == 0;
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        if (_trip is not { } trip)
            return;
        var strings = LocalizationResourceManager.Instance;
        await RunAsync(async () =>
        {
            var name = await PromptForNameAsync(strings["AddNamedPackingList"]);
            if (name is null)
                return;
            await Repository.CreatePackingListAsync(trip, name);
            await LoadAsync(trip, refresh: false);
        });
    }

    private async void OnOpenListClicked(object? sender, EventArgs e)
    {
        if (_trip is not { } trip || sender is not Button { BindingContext: PackingListCard card })
            return;
        await RunAsync(() => Shell.Current.GoToAsync(PackingListPage.EditorRoute, new ShellNavigationQueryParameters
        {
            ["trip"] = trip,
            ["list"] = card.List
        }));
    }

    private async void OnListOptionsClicked(object? sender, EventArgs e)
    {
        if (_trip is not { } trip || sender is not Button { BindingContext: PackingListCard card })
            return;

        var strings = LocalizationResourceManager.Instance;
        var rename = strings["RenamePackingList"];
        var delete = strings["DeleteNamedPackingList"];
        await RunAsync(async () =>
        {
            var selection = await OptionsPopup.ChooseAsync(card.DisplayName, [rename, delete]);
            if (selection == rename)
            {
                var name = await PromptForNameAsync(rename, card.DisplayName);
                if (name is null)
                    return;
                await Repository.RenamePackingListAsync(trip, card.List.Id, name);
            }
            else if (selection == delete)
            {
                var confirmed = await ConfirmPopup.AskAsync(delete,
                    string.Format(strings["DeleteNamedPackingListConfirm"], card.DisplayName),
                    strings["Delete"], strings["Cancel"]);
                if (!confirmed)
                    return;
                await Repository.DeletePackingListAsync(trip, card.List.Id);
            }
            else
                return;

            await LoadAsync(trip, refresh: false);
        });
    }

    private static async Task<string?> PromptForNameAsync(string title, string initialValue = "")
    {
        var strings = LocalizationResourceManager.Instance;
        var name = await Shell.Current.DisplayPromptAsync(title, strings["PackingListName"],
            strings["SaveChanges"], strings["Cancel"], maxLength: PackingListDTO.MaxNameLength, initialValue: initialValue);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_working)
            return;
        _working = true;
        AddListButton.IsEnabled = ListCards.IsEnabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            var strings = LocalizationResourceManager.Instance;
            await ConfirmPopup.ShowMessageAsync(strings["Error"], strings["ManagePackingListsError"]);
        }
        finally
        {
            _working = false;
            AddListButton.IsEnabled = ListCards.IsEnabled = true;
        }
    }
}

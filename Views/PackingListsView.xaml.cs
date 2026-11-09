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
    private int _loadVersion;
    private IReadOnlyList<PackingListDTO>? _shownLists;

    public IPackingListRepository Repository { private get; set; } = null!;

    public PackingListsView() => InitializeComponent();

    public async Task ShowAsync(TripDTO? trip)
    {
        _trip = trip;
        if (_working)
            return;
        var version = ++_loadVersion;
        if (trip is null)
            return;

        try
        {
            var cached = await Repository.GetPackingListsAsync(trip, refresh: false);
            if (version != _loadVersion || _working)
                return;
            if (cached.Count > 0 || _shownLists is not null)
                DisplayLists(cached);
            _ = RefreshAsync(trip, version);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    public Task UpdateTripAsync(TripDTO? trip)
    {
        if (_trip?.LocalTripId != trip?.LocalTripId || _trip?.RemoteTripId != trip?.RemoteTripId
            || _trip?.LocalUserId != trip?.LocalUserId)
            return ShowAsync(trip);
        _trip = trip;
        return Task.CompletedTask;
    }

    private async Task RefreshAsync(TripDTO trip, int version)
    {
        try
        {
            await Repository.RefreshPackingTripAsync(trip);
            if (version != _loadVersion || _working)
                return;
            var refreshed = await Repository.GetPackingListsAsync(trip, refresh: false);
            if (version == _loadVersion && !_working)
                DisplayLists(refreshed);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task LoadAsync(TripDTO trip)
    {
        var version = ++_loadVersion;
        var lists = await Repository.GetPackingListsAsync(trip, refresh: false);
        if (version == _loadVersion)
            DisplayLists(lists);
    }

    private void DisplayLists(IReadOnlyList<PackingListDTO> lists)
    {
        if (_shownLists?.SequenceEqual(lists) == true)
            return;
        _shownLists = lists;
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
            await LoadAsync(trip);
        });
    }

    private async void OnOpenListClicked(object? sender, EventArgs e)
    {
        if (_trip is not { } trip || sender is not Button { BindingContext: PackingListCard card })
            return;
        await RunAsync(async () =>
        {
            var items = await Repository.GetPackingListItemsAsync(trip, card.List.Id, refresh: false);
            await Shell.Current.GoToAsync(PackingListPage.EditorRoute, new ShellNavigationQueryParameters
            {
                ["trip"] = trip,
                ["list"] = card.List,
                ["items"] = items
            });
        });
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

            await LoadAsync(trip);
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

using Packo.Extensions;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Mvvm.Input;
using Packo.Helpers;
using Packo.Interfaces;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.Enums;
using Packo.Repositories.Enums;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.Popups;
using System.Globalization;
using System.Windows.Input;

namespace Packo.ViewModels
{
    public class PackingListViewModel : BaseViewModel
    {
        public ObservableRangeCollection<PackingItem> Items { get; } = new();

        private int _remoteTripId { get; set; }
        private int _localTripId { get; set; }
        private TripDTO? _trip;
        private PackingListDTO? _list;
        private readonly IPackingListRepository _packingLists;
        public string ListName => _list?.Name ?? LocalizationResourceManager.Instance["PackingListTitle"];
        private bool _channelsInitialized;
        private bool _isLoading;
        private int _itemsVersion;
        private readonly IPackingSuggestionService _packingSuggestionService;
        private bool _isDeleting;
        private bool _isEditingItem;
        private bool _isAddingItems;
        private List<PackingItemDTO>? _itemsBeingAdded;
        private PackingItemSortMode _sortMode;
        public PackingItemSortMode SortMode => _sortMode;
        private bool IsSortingByQuantity => SortMode is PackingItemSortMode.QuantityAscending or PackingItemSortMode.QuantityDescending;
        public bool CanEditItems => _channelsInitialized && !_isLoading && !_isDeleting && !_isEditingItem && !IsAddingItems;
        public bool HasItems => Items.Count > 0;
        public string TripDestination => _trip?.Destination ?? string.Empty;
        public string TripDatesRange => TripSchedule.FormatRange(_trip?.StartDate, _trip?.EndDate);
        public bool HasTripDestination => !string.IsNullOrWhiteSpace(TripDestination);
        public bool HasTripDates => _trip?.StartDate.HasValue == true || _trip?.EndDate.HasValue == true;

        private bool IsAddingItems
        {
            get => _isAddingItems;
            set
            {
                SetProperty(ref _isAddingItems, value);
                NotifyItemStateChanged();
            }
        }

        private bool _canGeneratePackingList;
        public bool CanGeneratePackingList
        {
            get => _canGeneratePackingList;
            private set => SetProperty(ref _canGeneratePackingList, value);
        }

        private bool _canCopyPackingList;
        public bool CanCopyPackingList
        {
            get => _canCopyPackingList;
            private set => SetProperty(ref _canCopyPackingList, value);
        }

        private bool _isGeneratingPackingList;
        public bool IsGeneratingPackingList
        {
            get => _isGeneratingPackingList;
            private set => SetProperty(ref _isGeneratingPackingList, value);
        }

        private string _newItemName = string.Empty;
        public string NewItemName
        {
            get => _newItemName;
            set
            {
                if (_newItemName != value)
                {
                    _newItemName = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand AddItemCommand => new Command(async () => await AddItemAsync());
        public ICommand GeneratePackingListCommand => new Command(async () => await GeneratePackingListAsync());
        public ICommand CopyPackingListCommand => new Command(async () => await CopyPackingListAsync());
        public ICommand ToggleIsPackedCommand => new Command<PackingItem>(async packingItem => await ToggleIsPackedAsync(packingItem));
        public ICommand EditQuantityCommand => new Command<PackingItem>(async item => await EditQuantityAsync(item));
        public ICommand DeleteItemCommand => new Command<PackingItem>(async item => await DeleteItemsAsync(item));
        public ICommand ClearListCommand => new Command(async () => await DeleteItemsAsync(null));
        public IAsyncRelayCommand SelectSortCommand { get; }

        public PackingListViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, IPackingSuggestionService packingSuggestionService, IPackingListRepository packingLists) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _packingSuggestionService = packingSuggestionService;
            _packingLists = packingLists;
            Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
            SelectSortCommand = new AsyncRelayCommand(SelectSortAsync);
        }

        private async Task SelectSortAsync()
        {
            var strings = LocalizationResourceManager.Instance;
            var modes = new[]
            {
                (Mode: PackingItemSortMode.UnpackedFirst, Label: strings["PackingSortUnpacked"]),
                (Mode: PackingItemSortMode.PackedFirst, Label: strings["PackingSortPacked"]),
                (Mode: PackingItemSortMode.NameAscending, Label: strings["PackingSortNameAscending"]),
                (Mode: PackingItemSortMode.NameDescending, Label: strings["PackingSortNameDescending"]),
                (Mode: PackingItemSortMode.QuantityDescending, Label: strings["PackingSortQuantityDescending"]),
                (Mode: PackingItemSortMode.QuantityAscending, Label: strings["PackingSortQuantityAscending"])
            };
            var options = modes.Select(option => option.Mode == SortMode ? $"✓  {option.Label}" : option.Label).ToArray();
            var selection = await OptionsPopup.ChooseAsync(strings["PackingSortTitle"], options);
            var index = Array.IndexOf(options, selection);
            if (index < 0 || !_channelsInitialized || !SetProperty(ref _sortMode, modes[index].Mode, nameof(SortMode)))
                return;

            var ordered = PackingItemOrder.Sort(Items, SortMode).ToList();
            if (!Items.SequenceEqual(ordered))
                Items.ReplaceRange(ordered);
        }

        private async Task DeleteItemsAsync(PackingItem? item)
        {
            if (!CanEditItems || _isLoading || !HasItems)
                return;

            _isDeleting = true;
            NotifyItemStateChanged();
            var remainingItems = new List<PackingItem>();
            var clearedView = false;
            try
            {
                var targets = item is null ? Items.ToList() : new List<PackingItem> { item };
                var resources = LocalizationResourceManager.Instance;
                var confirmed = await ConfirmPopup.AskAsync(
                    resources[item is null ? "ClearPackingList" : "DeletePackingItem"],
                    string.Format(System.Globalization.CultureInfo.CurrentUICulture,
                        resources[item is null ? "ClearPackingListConfirm" : "DeletePackingItemConfirm"],
                        item is null ? (object)targets.Count : item.Name),
                    resources["Delete"], resources["Cancel"]);
                if (!confirmed)
                    return;

                if (item is null)
                {
                    remainingItems.AddRange(targets);
                    // One reset notification removes the whole list without per-row animations.
                    Items.Clear();
                    clearedView = true;
                    var dtos = targets.Select(target =>
                    {
                        var dto = Mappers.MapToPackingItemDTO(target);
                        dto.LocalTripId = _localTripId;
                        dto.RemoteTripId = _remoteTripId;
                        dto.LocalUserId = Session.LocalUserId ?? string.Empty;
                        dto.RemoteUserId = Session.UserId ?? string.Empty;
                        return dto;
                    }).ToList();
                    await _packingItemRepository.DeletePackingItemsAsync(dtos);
                    remainingItems.Clear();
                    return;
                }

                foreach (var target in targets)
                {
                    var dto = Mappers.MapToPackingItemDTO(target);
                    dto.LocalTripId = _localTripId;
                    dto.RemoteTripId = _remoteTripId;
                    dto.LocalUserId = Session.LocalUserId ?? string.Empty;
                    dto.RemoteUserId = Session.UserId ?? string.Empty;
                    await _packingItemRepository.DeletePackingItemAsync(dto);
                    remainingItems.Remove(target);
                    if (clearedView)
                        continue;

                    // Update immediately, including offline and when realtime is unavailable.
                    var current = Items.FirstOrDefault(x => target.RemoteId > 0
                        ? x.RemoteId == target.RemoteId : x.LocalId == target.LocalId);
                    if (current != null)
                        Items.Remove(current);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                if (clearedView)
                {
                    // Restore only failed/unprocessed items; keep any new realtime arrivals.
                    var toRestore = remainingItems.Where(target => !Items.Any(x => target.RemoteId > 0
                        ? x.RemoteId == target.RemoteId : x.LocalId == target.LocalId)).ToList();
                    Items.AddRange(toRestore, System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
                    SortItems();
                }
                await ConfirmPopup.ShowMessageAsync(LocalizationResourceManager.Instance["Error"],
                    LocalizationResourceManager.Instance["DeletePackingItemsError"]);
            }
            finally
            {
                _isDeleting = false;
                NotifyItemStateChanged();
            }
        }

        private async Task AddItemAsync()
        {
            if (!CanEditItems || _list is null || string.IsNullOrWhiteSpace(NewItemName))
                return;

            var newItem = new PackingItemDTO { Name = NewItemName, PackingListId = _list.Id, RemoteTripId = _remoteTripId, LocalTripId = _localTripId, RemoteUserId = Session.UserId, LocalUserId = Session.LocalUserId, CreatedDate = DateTime.Now };

            IsAddingItems = true;
            try
            {
                await _packingItemRepository.AddPackingItemAsync(newItem);
                NewItemName = string.Empty;
                var item = Mappers.MapToPackingItem(newItem);
                Items.Add(item);
                SortItems(item);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                await ConfirmPopup.ShowMessageAsync(LocalizationResourceManager.Instance["Error"],
                    LocalizationResourceManager.Instance["AddPackingItemsError"]);
            }
            finally
            {
                IsAddingItems = false;
            }
        }

        private async Task GeneratePackingListAsync()
        {
            RefreshTripSchedule();
            if (!CanEditItems || !CanGeneratePackingList)
                return;

            IsAddingItems = true;
            try
            {
                var confirmed = await ConfirmPopup.AskAsync(LocalizationResourceManager.Instance["SuggestedPackingTitle"]);
                if (!confirmed)
                    return;

                IsGeneratingPackingList = true;
                // Let MAUI render the loading overlay before the AI request starts.
                await Task.Yield();

                var localUserId = Session.LocalUserId ?? string.Empty;
                var trip = _remoteTripId == 0
                    ? await _tripRepository.GetLocalTripAsync(_localTripId, localUserId)
                    : await _tripRepository.GetTripAsync(new TripDTO
                    {
                        LocalTripId = _localTripId,
                        RemoteTripId = _remoteTripId,
                        LocalUserId = localUserId,
                        RemoteUserId = Session.UserId ?? string.Empty
                    });

                if (_remoteTripId > 0 && trip?.RemoteTripId != _remoteTripId)
                    return;

                SetTrip(trip);
                if (!_channelsInitialized || trip?.StartDate is not DateTime start || trip.EndDate is not DateTime end
                    || !CanGeneratePackingList)
                    return;

                var suggestedItems = await _packingSuggestionService.GenerateItemsAsync(
                    trip.Destination,
                    start,
                    end,
                    trip.AdultsCount,
                    trip.ChildrenAges,
                    trip.SelectedTransportTypes,
                    trip.TripType,
                    LocalizationResourceManager.Instance.CurrentLanguage);

                IsGeneratingPackingList = false;
                var popup = new PackingItemsPopup(suggestedItems);
                await Application.Current.MainPage.ShowPopupAsync(popup);
                await AddSelectedItemsAsync(popup.SelectedItems);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                await ConfirmPopup.ShowMessageAsync(LocalizationResourceManager.Instance["Error"],
                    LocalizationResourceManager.Instance["AddPackingItemsError"]);
            }
            finally
            {
                IsGeneratingPackingList = false;
                IsAddingItems = false;
            }
        }

        public void RefreshTripSchedule()
        {
            var tripIsOpen = _trip is { IsInTrash: false }
                && TripSchedule.GetStatus(_trip.StartDate, _trip.EndDate, DateTime.Now) != TripStatus.Completed;
            CanCopyPackingList = tripIsOpen;
            CanGeneratePackingList = tripIsOpen && _trip is { StartDate: not null, EndDate: not null }
                && !string.IsNullOrWhiteSpace(_trip.Destination);
        }

        private void SetTrip(TripDTO? trip)
        {
            _trip = trip;
            OnPropertyChanged(nameof(TripDestination));
            OnPropertyChanged(nameof(TripDatesRange));
            OnPropertyChanged(nameof(HasTripDestination));
            OnPropertyChanged(nameof(HasTripDates));
            RefreshTripSchedule();
        }

        private async Task CopyPackingListAsync()
        {
            RefreshTripSchedule();
            if (!CanEditItems || !CanCopyPackingList || _trip is null || _list is null)
                return;

            IsAddingItems = true;
            var strings = LocalizationResourceManager.Instance;
            var title = strings["CopyPackingList"];
            try
            {
                var trips = await _tripRepository.GetActiveTripsWithStatsAsync();
                var sources = trips.Select(row => row.Trip)
                    .Where(trip => !trip.IsInTrash && !IsCurrentPackingTrip(trip))
                    .OrderByDescending(trip => trip.CreatedDate)
                    .ToList();

                if (sources.Count == 0)
                {
                    await ConfirmPopup.ShowMessageAsync(title, strings["NoPackingSource"]);
                    return;
                }

                var options = sources.Select((trip, index) =>
                {
                    var date = (trip.StartDate ?? trip.CreatedDate).ToString("g", CultureInfo.CurrentUICulture);
                    return $"{index + 1}. {trip.Destination} · {date}";
                }).ToArray();
                var selection = await OptionsPopup.ChooseAsync(strings["ChoosePackingSource"], options);
                var sourceIndex = Array.IndexOf(options, selection);
                if (sourceIndex < 0 || !_channelsInitialized)
                    return;

                var source = sources[sourceIndex];
                var sourceLists = await _packingLists.GetPackingListsAsync(source);
                if (sourceLists.Count == 0)
                {
                    await ConfirmPopup.ShowMessageAsync(title, strings["EmptyPackingSource"]);
                    return;
                }

                IReadOnlyCollection<Guid> sourceListIds = [sourceLists[0].Id];
                if (sourceLists.Count > 1)
                {
                    string[] listOptions =
                    [
                        strings["CopyFromAllPackingLists"],
                        .. sourceLists.Select((list, index) =>
                            $"{index + 1}. {list.Name ?? strings["PackingListTitle"]} · {list.Packed}/{list.Total}")
                    ];
                    var listSelection = await OptionsPopup.ChooseAsync(strings["ChoosePackingSourceList"], listOptions);
                    var listIndex = Array.IndexOf(listOptions, listSelection);
                    if (listIndex < 0)
                        return;

                    sourceListIds = listIndex == 0
                        ? sourceLists.Select(list => list.Id).ToArray()
                        : [sourceLists[listIndex - 1].Id];
                }
                if (!_channelsInitialized)
                    return;

                var sourceItems = await _packingLists.GetPackingListItemsAsync(source, sourceListIds, refresh: false);
                if (!_channelsInitialized)
                    return;

                var candidates = sourceItems.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                    .Select(item => new PackingSuggestion { Name = item.Name.Trim(), Quantity = Math.Max(1, item.Quantity) })
                    .ToList();
                if (candidates.Count == 0)
                {
                    await ConfirmPopup.ShowMessageAsync(title, strings["EmptyPackingSource"]);
                    return;
                }

                var available = PackingListCopy.ExcludeExisting(candidates, Items.Select(item => item.Name));
                var skippedSelected = 0;
                var added = 0;
                if (available.Count > 0)
                {
                    var popup = new PackingItemsPopup(available, title);
                    await Application.Current.MainPage.ShowPopupAsync(popup);
                    if (popup.SelectedItems.Count == 0 || !_channelsInitialized)
                        return;

                    var latest = await _packingLists.GetPackingListItemsAsync(_trip, _list.Id);
                    RefreshTripSchedule();
                    if (!CanCopyPackingList)
                        return;

                    var missing = PackingListCopy.ExcludeExisting(popup.SelectedItems,
                        latest.Select(item => item.Name).Concat(Items.Select(item => item.Name)));
                    skippedSelected = popup.SelectedItems.Count - missing.Count;
                    await AddSelectedItemsAsync(missing);
                    added = missing.Count;
                }

                if (_channelsInitialized)
                {
                    var summaryTitle = strings[added > 0 ? "CopyPackingDone" : "CopyPackingAlreadyOnList"];
                    var summary = added switch
                    {
                        0 => strings["CopyPackingAlreadyOnListMessage"],
                        1 => strings["CopyPackingAddedOne"],
                        _ => string.Format(CultureInfo.CurrentUICulture, strings["CopyPackingAddedMany"], added)
                    };
                    if (added > 0 && skippedSelected > 0)
                    {
                        var skippedMessage = skippedSelected == 1
                            ? strings["CopyPackingSkippedOne"]
                            : string.Format(CultureInfo.CurrentUICulture, strings["CopyPackingSkippedMany"], skippedSelected);
                        summary += "\n\n" + skippedMessage;
                    }

                    await ConfirmPopup.ShowMessageAsync(summaryTitle, summary);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                await ConfirmPopup.ShowMessageAsync(strings["Error"], strings["CopyPackingError"]);
            }
            finally
            {
                IsAddingItems = false;
            }
        }

        private bool IsCurrentPackingTrip(TripDTO trip) => trip.RemoteTripId > 0 && _remoteTripId > 0
            ? trip.RemoteTripId == _remoteTripId
            : trip.LocalTripId == _localTripId;

        private async Task AddSelectedItemsAsync(List<PackingSuggestion> selectedItems)
        {
            if (!_channelsInitialized || _list is null || selectedItems.Count == 0)
                return;

            var createdDate = DateTime.Now;
            var newItems = selectedItems.Select(item => new PackingItemDTO
            {
                Name = item.Name,
                Quantity = item.Quantity,
                PackingListId = _list.Id,
                RemoteTripId = _remoteTripId,
                LocalTripId = _localTripId,
                RemoteUserId = Session.UserId ?? string.Empty,
                LocalUserId = Session.LocalUserId ?? string.Empty,
                CreatedDate = createdDate
            }).ToList();

            _itemsBeingAdded = newItems;
            try
            {
                await _packingItemRepository.AddPackingItemsAsync(newItems);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    Items.ReplaceRange(PackingItemOrder.Sort(Items.Concat(newItems.Select(Mappers.MapToPackingItem)), SortMode).ToList());
                });
            }
            finally
            {
                _itemsBeingAdded = null;
            }
        }

        private async Task ToggleIsPackedAsync(PackingItem packingItem)
        {
            if (CanEditItems && packingItem != null && Items.Contains(packingItem))
            {
                _isEditingItem = true;
                NotifyItemStateChanged();
                var original = !packingItem.IsPacked;
                try
                {
                    // Let the native checkbox finish its event before moving its row.
                    await Task.Yield();
                    // Move the changed row without resetting the collection or scroll position.
                    SortItems(packingItem);
                    await _packingItemRepository.UpdatePackingItemAsync(Mappers.MapToPackingItemDTO(packingItem));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                    packingItem.IsPacked = original;
                    SortItems(packingItem);
                    await ShowUpdateErrorAsync();
                }
                finally
                {
                    _isEditingItem = false;
                    NotifyItemStateChanged();
                }
            }
        }

        private async Task EditQuantityAsync(PackingItem item)
        {
            if (!CanEditItems || item is null || !Items.Contains(item)) return;
            _isEditingItem = true;
            NotifyItemStateChanged();
            var original = item.Quantity;
            try
            {
                // The popup edits a local draft; persist only its confirmed final value.
                var quantity = await PackingQuantityPrompt.ShowAsync(item.Name, item.Quantity);
                if (!quantity.HasValue || quantity.Value == item.Quantity || !Items.Contains(item)) return;
                original = item.Quantity;
                item.Quantity = quantity.Value;
                if (IsSortingByQuantity)
                    SortItems(item);
                await _packingItemRepository.UpdatePackingItemAsync(Mappers.MapToPackingItemDTO(item));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                item.Quantity = original;
                if (IsSortingByQuantity)
                    SortItems(item);
                await ShowUpdateErrorAsync();
            }
            finally
            {
                _isEditingItem = false;
                NotifyItemStateChanged();
            }
        }

        private static Task ShowUpdateErrorAsync() => ConfirmPopup.ShowMessageAsync(
            LocalizationResourceManager.Instance["Error"],
            LocalizationResourceManager.Instance["UpdatePackingItemError"]);

        private void SortItems(PackingItem? changedItem = null) => PackingItemOrder.Apply(Items, changedItem, SortMode);

        //private async Task<bool> CheckItemExist(string newItemName)
        //{
        //    var response = await _supabase.Client.From<PackingItem>().Where(x => x.TripId == _tripId).Get();

        //    if (response.Models == null || !response.Models.Any())
        //        return false;

        //    return response.Models.Any(x => x.Name == newItemName);
        //}

        public Task DisposeRealtimeAsync()
        {
            if (_subscription != null)
            {
                _subscription.Unsubscribe();
            }

            return Task.CompletedTask;
        }

        private void OnPackingItemChanged(PackingItemChange change)
        {
            if (change.Type == PackingItemChangeType.Delete)
            {
                // DELETE payloads may only contain the primary key.
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (!_channelsInitialized || change.Item.RemotePackingItemId is not int deletedId)
                        return;
                    var deleted = Items.FirstOrDefault(x => x.RemoteId == deletedId);
                    if (deleted != null)
                    {
                        _itemsVersion++;
                        Items.Remove(deleted);
                    }
                });
                return;
            }
            var matchesTrip = _remoteTripId > 0
                ? change.Item.RemoteTripId == _remoteTripId
                : change.Item.LocalTripId == _localTripId && change.Item.LocalUserId == Session.LocalUserId;
            if (change.Item.PackingListId != _list?.Id || !matchesTrip)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_channelsInitialized)
                    return;
                _itemsVersion++;

                // Upload/realtime may finish between the local commit and the UI batch update.
                var pendingAddition = change.Item.LocalPackingItemId > 0
                    ? _itemsBeingAdded?.FirstOrDefault(x => x.LocalPackingItemId == change.Item.LocalPackingItemId)
                    : null;
                if (pendingAddition != null && (change.Type == PackingItemChangeType.Insert
                    || change.Type == PackingItemChangeType.IdentityAssigned))
                {
                    pendingAddition.RemotePackingItemId = change.Item.RemotePackingItemId;
                    pendingAddition.RemoteTripId = change.Item.RemoteTripId;
                    pendingAddition.RemoteUserId = change.Item.RemoteUserId;
                    return;
                }
                switch (change.Type)
                {
                    case PackingItemChangeType.IdentityAssigned:
                        var localItem = Items.FirstOrDefault(x => x.LocalId == change.Item.LocalPackingItemId);
                        if (localItem != null)
                        {
                            localItem.RemoteId = change.Item.RemotePackingItemId;
                            localItem.RemoteTripId = change.Item.RemoteTripId;
                            localItem.RemoteUserId = change.Item.RemoteUserId;
                            SortItems(localItem);
                        }
                        break;
                    case PackingItemChangeType.Insert:
                        var existing = Items.FirstOrDefault(x => x.RemoteId == change.Item.RemotePackingItemId
                            || (change.Item.LocalPackingItemId > 0 && x.LocalId == change.Item.LocalPackingItemId));
                        if (existing != null)
                            existing.RemoteId = change.Item.RemotePackingItemId;
                        else
                            Items.Add(MapRealtimeItem(change.Item));
                        SortItems();
                        break;

                    case PackingItemChangeType.Update:
                        var item = Items.FirstOrDefault(x => x.RemoteId == change.Item.RemotePackingItemId);
                        if (item != null)
                        {
                            item.Name = change.Item.Name;
                            item.Quantity = Math.Max(1, change.Item.Quantity);
                            item.IsPacked = change.Item.IsPacked;
                            SortItems(item);
                        }

                        break;

                    case PackingItemChangeType.Delete:
                        var toRemove = Items.FirstOrDefault(i => i.RemoteId == change.Item.RemotePackingItemId);
                        if (toRemove != null)
                            Items.Remove(toRemove);
                        break;
                }
            });
        }

        private PackingItem MapRealtimeItem(PackingItemDTO dto)
        {
            var item = Mappers.MapToPackingItem(dto);
            item.LocalTripId = _localTripId;
            item.LocalUserId = Session.LocalUserId ?? string.Empty;
            return item;
        }

        private void NotifyItemStateChanged()
        {
            _itemsVersion++;
            OnPropertyChanged(nameof(CanEditItems));
        }

        private void DisplayItems(IReadOnlyList<PackingItemDTO> items)
        {
            var sorted = PackingItemOrder.Sort(items.Select(Mappers.MapToPackingItem), SortMode).ToList();
            if (Items.Count != sorted.Count || !Items.Zip(sorted, SameItem).All(same => same))
                Items.ReplaceRange(sorted);
        }

        private static bool SameItem(PackingItem current, PackingItem updated) =>
            current.LocalId == updated.LocalId && current.RemoteId == updated.RemoteId
            && current.RemoteTripId == updated.RemoteTripId && current.RemoteUserId == updated.RemoteUserId
            && current.Name == updated.Name && current.IsPacked == updated.IsPacked
            && current.Quantity == updated.Quantity && current.CreatedDate == updated.CreatedDate;

        private async Task RefreshItemsAsync(TripDTO trip, Guid listId)
        {
            var version = _itemsVersion;
            try
            {
                await _packingLists.RefreshPackingTripAsync(trip);
                if (version != _itemsVersion || !CanEditItems)
                    return;

                // A refresh must not replace a newer edit or realtime change on this page.
                var items = await _packingLists.GetPackingListItemsAsync(trip, listId, refresh: false);
                if (version == _itemsVersion && CanEditItems)
                    DisplayItems(items);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        protected override async Task OnNavigatedToAsync(IDictionary<string, object> query)
        {
            if (query.TryGetValue("trip", out var value) && value is TripDTO trip
                && query.TryGetValue("list", out var selected) && selected is PackingListDTO list)
            {
                if (_channelsInitialized && trip.RemoteTripId == _remoteTripId && trip.LocalTripId == _localTripId
                    && list.Id == _list?.Id)
                    return;

                _packingItemRepository.PackingItemChanged -= OnPackingItemChanged;
                _remoteTripId = trip.RemoteTripId;
                _localTripId = trip.LocalTripId;
                _list = list;
                OnPropertyChanged(nameof(ListName));
                SetTrip(trip);

                _channelsInitialized = true;
                _isLoading = true;
                NotifyItemStateChanged();
                var version = _itemsVersion;
                try
                {
                    var items = query.TryGetValue("items", out var snapshot) && snapshot is IReadOnlyList<PackingItemDTO> cached
                        ? cached
                        : await _packingLists.GetPackingListItemsAsync(trip, list.Id, refresh: false);
                    if (version == _itemsVersion)
                        DisplayItems(items);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

                if (version != _itemsVersion)
                    return;

                _isLoading = false;
                NotifyItemStateChanged();
                _packingItemRepository.PackingItemChanged += OnPackingItemChanged;
                if (Session.IsAuthenticated)
                    _ = StartRealtimeAsync();
                _ = RefreshItemsAsync(trip, list.Id);
            }
        }

        private async Task StartRealtimeAsync()
        {
            try { await _packingItemRepository.StartRealtimeAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        public override Task OnNavigatedFromAsync(IDictionary<string, object> query)
        {
            _packingItemRepository.PackingItemChanged -= OnPackingItemChanged;
            _channelsInitialized = false;
            _isLoading = false;
            NotifyItemStateChanged();
            return Task.CompletedTask;
        }
    }
}

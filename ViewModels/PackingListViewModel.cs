using Packo.Extensions;
using CommunityToolkit.Maui.Extensions;
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
        private bool _channelsInitialized;
        private bool _isLoading;
        private readonly IPackingSuggestionService _packingSuggestionService;
        private bool _isDeleting;
        private bool _isEditingItem;
        private bool _isAddingItems;
        private List<PackingItemDTO>? _itemsBeingAdded;
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
                OnPropertyChanged(nameof(CanEditItems));
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

        public PackingListViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, IPackingSuggestionService packingSuggestionService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _packingSuggestionService = packingSuggestionService;
            Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
        }

        private async Task DeleteItemsAsync(PackingItem? item)
        {
            if (!CanEditItems || _isLoading || !HasItems)
                return;

            _isDeleting = true;
            OnPropertyChanged(nameof(CanEditItems));
            var remainingItems = new List<PackingItem>();
            var clearedView = false;
            try
            {
                var targets = item is null ? Items.ToList() : new List<PackingItem> { item };
                var resources = LocalizationResourceManager.Instance;
                var confirmed = await Shell.Current.DisplayAlertAsync(
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
                await Shell.Current.DisplayAlertAsync(LocalizationResourceManager.Instance["Error"],
                    LocalizationResourceManager.Instance["DeletePackingItemsError"],
                    LocalizationResourceManager.Instance["Ok"]);
            }
            finally
            {
                _isDeleting = false;
                OnPropertyChanged(nameof(CanEditItems));
            }
        }

        private async Task AddItemAsync()
        {
            if (!CanEditItems || string.IsNullOrWhiteSpace(NewItemName))
                return;

            var newItem = new PackingItemDTO { Name = NewItemName, RemoteTripId = _remoteTripId, LocalTripId = _localTripId, RemoteUserId = Session.UserId, LocalUserId = Session.LocalUserId, CreatedDate = DateTime.Now };

            IsAddingItems = true;
            try
            {
                await _packingItemRepository.AddPackingItemAsync(newItem);
                NewItemName = string.Empty;
                Items.Add(Mappers.MapToPackingItem(newItem));
                SortItems();
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
                var confirmation = await Application.Current.MainPage.ShowPopupAsync<bool>(
                    new ConfirmPopup(LocalizationResourceManager.Instance["SuggestedPackingTitle"], string.Empty));
                if (!confirmation.Result)
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
                await Shell.Current.DisplayAlertAsync(LocalizationResourceManager.Instance["Error"],
                    LocalizationResourceManager.Instance["AddPackingItemsError"],
                    LocalizationResourceManager.Instance["Ok"]);
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
            if (!CanEditItems || !CanCopyPackingList)
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
                    await Shell.Current.DisplayAlertAsync(title, strings["NoPackingSource"], strings["Ok"]);
                    return;
                }

                var options = sources.Select((trip, index) =>
                {
                    var date = (trip.StartDate ?? trip.CreatedDate).ToString("g", CultureInfo.CurrentUICulture);
                    return $"{index + 1}. {trip.Destination} · {date}";
                }).ToArray();
                var selection = await Shell.Current.DisplayActionSheetAsync(strings["ChoosePackingSource"],
                    strings["Cancel"], null, options);
                var sourceIndex = Array.IndexOf(options, selection);
                if (sourceIndex < 0 || !_channelsInitialized)
                    return;

                var source = sources[sourceIndex];
                var sourceItems = await _packingItemRepository.GetPackingItemsForTripAsync(source.LocalTripId, source.RemoteTripId);
                if (!_channelsInitialized)
                    return;

                var candidates = sourceItems.Where(item => !string.IsNullOrWhiteSpace(item.Name))
                    .Select(item => new PackingSuggestion { Name = item.Name.Trim(), Quantity = Math.Max(1, item.Quantity) })
                    .ToList();
                if (candidates.Count == 0)
                {
                    await Shell.Current.DisplayAlertAsync(title, strings["EmptyPackingSource"], strings["Ok"]);
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

                    var latest = await _packingItemRepository.GetPackingItemsForTripAsync(_localTripId, _remoteTripId);
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

                    await Shell.Current.DisplayAlertAsync(summaryTitle, summary, strings["Ok"]);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                await Shell.Current.DisplayAlertAsync(strings["Error"], strings["CopyPackingError"], strings["Ok"]);
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
            if (!_channelsInitialized || selectedItems.Count == 0)
                return;

            var createdDate = DateTime.Now;
            var newItems = selectedItems.Select(item => new PackingItemDTO
            {
                Name = item.Name,
                Quantity = item.Quantity,
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
                    Items.ReplaceRange(PackingItemOrder.Sort(Items.Concat(newItems.Select(Mappers.MapToPackingItem))).ToList());
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
                OnPropertyChanged(nameof(CanEditItems));
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
                    OnPropertyChanged(nameof(CanEditItems));
                }
            }
        }

        private async Task EditQuantityAsync(PackingItem item)
        {
            if (!CanEditItems || item is null || !Items.Contains(item)) return;
            _isEditingItem = true;
            OnPropertyChanged(nameof(CanEditItems));
            var original = item.Quantity;
            try
            {
                // The popup edits a local draft; persist only its confirmed final value.
                var quantity = await PackingQuantityPrompt.ShowAsync(item.Name, item.Quantity);
                if (!quantity.HasValue || quantity.Value == item.Quantity || !Items.Contains(item)) return;
                original = item.Quantity;
                item.Quantity = quantity.Value;
                await _packingItemRepository.UpdatePackingItemAsync(Mappers.MapToPackingItemDTO(item));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                item.Quantity = original;
                await ShowUpdateErrorAsync();
            }
            finally
            {
                _isEditingItem = false;
                OnPropertyChanged(nameof(CanEditItems));
            }
        }

        private static Task ShowUpdateErrorAsync() => Shell.Current.DisplayAlertAsync(
            LocalizationResourceManager.Instance["Error"],
            LocalizationResourceManager.Instance["UpdatePackingItemError"],
            LocalizationResourceManager.Instance["Ok"]);

        private void SortItems(PackingItem? changedItem = null) => PackingItemOrder.Apply(Items, changedItem);

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
                    var deleted = Items.FirstOrDefault(x => x.RemoteId == change.Item.RemotePackingItemId);
                    if (deleted != null)
                        Items.Remove(deleted);
                });
                return;
            }
            if (change.Item.RemoteTripId != _remoteTripId)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
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

        private async Task LoadData()
        {
            if (_isLoading)
                return;

            _isLoading = true;
            SetBusy(true);

            try
            {
                var tripsWithStats = await _packingItemRepository.GetPackingItemsForTripAsync(_localTripId, _remoteTripId);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Items.ReplaceRange(PackingItemOrder.Sort(tripsWithStats.Select(Mappers.MapToPackingItem)).ToList());
                });
            }
            finally
            {
                SetBusy(false);
                _isLoading = false;
                OnPropertyChanged(nameof(CanEditItems));
            }
        }

        protected override async Task OnNavigatedToAsync(IDictionary<string, object> query)
        {
            if (query.TryGetValue("trip", out var value) && value is TripDTO trip)
            {
                if (_channelsInitialized && trip.RemoteTripId == _remoteTripId && trip.LocalTripId == _localTripId)
                    return;

                _remoteTripId = trip.RemoteTripId;
                _localTripId = trip.LocalTripId;
                SetTrip(trip);

                if (Session.IsAuthenticated && !await _packingItemRepository.IsChannelCreatedAsync())
                {
                    await _packingItemRepository.StartRealtimeAsync();
                }

                _packingItemRepository.PackingItemChanged -= OnPackingItemChanged;
                _packingItemRepository.PackingItemChanged += OnPackingItemChanged;

                _channelsInitialized = true;

                await LoadData();

            }
        }

        public override Task OnNavigatedFromAsync(IDictionary<string, object> query)
        {
            _packingItemRepository.PackingItemChanged -= OnPackingItemChanged;
            _channelsInitialized = false;
            OnPropertyChanged(nameof(CanEditItems));
            return Task.CompletedTask;
        }
    }
}

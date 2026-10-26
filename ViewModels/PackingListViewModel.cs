using Packo.Extensions;
using CommunityToolkit.Maui.Extensions;
using Packo.Helpers;
using Packo.Interfaces;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Repositories.Enums;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.Popups;
using System.Windows.Input;

namespace Packo.ViewModels
{
    public class PackingListViewModel : BaseViewModel
    {
        public ObservableRangeCollection<PackingItem> Items { get; } = new();

        private int _remoteTripId { get; set; }
        private int _localTripId { get; set; }
        private bool _channelsInitialized;
        private bool _isLoading;
        private readonly IPackingSuggestionService _packingSuggestionService;
        private bool _isDeleting;
        private bool _isEditingItem;
        private List<PackingItemDTO>? _itemsBeingAdded;
        public bool CanEditItems => !_isDeleting && !_isEditingItem && !IsGeneratingPackingList;
        public bool HasItems => Items.Count > 0;

        private bool _isGeneratingPackingList;
        public bool IsGeneratingPackingList
        {
            get => _isGeneratingPackingList;
            private set
            {
                SetProperty(ref _isGeneratingPackingList, value);
                OnPropertyChanged(nameof(CanEditItems));
            }
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

            var newItem = new PackingItemDTO { Name = NewItemName, Category = 3, RemoteTripId = _remoteTripId, LocalTripId = _localTripId, RemoteUserId = Session.UserId, LocalUserId = Session.LocalUserId, CreatedDate = DateTime.Now };

            await _packingItemRepository.AddPackingItemAsync(newItem);

            NewItemName = string.Empty;

            Items.Add(Mappers.MapToPackingItem(newItem));
            SortItems();
        }

        private async Task GeneratePackingListAsync()
        {
            if (_isLoading || !CanEditItems)
                return;

            var confirmation = await Application.Current.MainPage.ShowPopupAsync<bool>(
                new ConfirmPopup(LocalizationResourceManager.Instance["SuggestedPackingTitle"], string.Empty));

            if (!confirmation.Result)
                return;

            IsGeneratingPackingList = true;
            try
            {
                // Let MAUI render the loading overlay before the AI request starts.
                await Task.Yield();

                var trip = await _tripRepository.GetTripAsync(new TripDTO
                {
                    LocalTripId = _localTripId,
                    RemoteTripId = _remoteTripId,
                    LocalUserId = Session.LocalUserId ?? string.Empty,
                    RemoteUserId = Session.UserId ?? string.Empty
                });

                if (trip?.StartDate is null || trip.EndDate is null || string.IsNullOrWhiteSpace(trip.Destination))
                    return;

                var suggestedItems = await _packingSuggestionService.GenerateItemsAsync(
                    trip.Destination,
                    trip.StartDate.Value,
                    trip.EndDate.Value,
                    trip.AdultsCount,
                    trip.ChildrenAges,
                    trip.SelectedTransportTypes,
                    trip.TripType,
                    LocalizationResourceManager.Instance.CurrentLanguage);

                var selectItemsPopup = new PackingItemsPopup(suggestedItems);
                _ = Application.Current.MainPage.ShowPopupAsync(selectItemsPopup);
                var selectedItems = await selectItemsPopup.ResultSource.Task;

                if (selectedItems.Count == 0)
                    return;

                var newItems = selectedItems.Select(item => new PackingItemDTO
                {
                    Name = item.Name,
                    Quantity = item.Quantity,
                    Category = 3,
                    RemoteTripId = _remoteTripId,
                    LocalTripId = _localTripId,
                    RemoteUserId = Session.UserId ?? string.Empty,
                    LocalUserId = Session.LocalUserId ?? string.Empty,
                    CreatedDate = DateTime.Now
                }).ToList();

                _itemsBeingAdded = newItems;
                await _packingItemRepository.AddPackingItemsAsync(newItems);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    Items.ReplaceRange(PackingItemOrder.Sort(Items.Concat(newItems.Select(Mappers.MapToPackingItem))).ToList());
                    _itemsBeingAdded = null;
                });
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
                _itemsBeingAdded = null;
                IsGeneratingPackingList = false;
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
                            item.Category = change.Item.Category;
                            item.ModifiedDate = change.Item.ModifiedDate;
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
            }
        }

        protected override async Task OnNavigatedToAsync(IDictionary<string, object> query)
        {
            if (query.TryGetValue("remoteTripId", out var remoteTripIdObj) && query.TryGetValue("localTripId", out var localTripIdObj))
            {
                var newRemoteTripId = Convert.ToInt32(remoteTripIdObj);
                var newLocalTripId = Convert.ToInt32(localTripIdObj);

                if (_channelsInitialized && newRemoteTripId == _remoteTripId && newLocalTripId == _localTripId)
                    return;

                _remoteTripId = newRemoteTripId;
                _localTripId = newLocalTripId;

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

        public override async Task OnNavigatedFromAsync(IDictionary<string, object> query)
        {
            _packingItemRepository.PackingItemChanged -= OnPackingItemChanged;
            _channelsInitialized = false;
        }
    }
}

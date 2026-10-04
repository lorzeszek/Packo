using Packo.Extensions;
using Packo.Helpers;
using Packo.Interfaces;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Repositories.Enums;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
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
        public ICommand ToggleIsPackedCommand => new Command<PackingItem>(async packingItem => await ToggleIsPackedAsync(packingItem));

        public PackingListViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
        }

        private async Task AddItemAsync()
        {
            if (string.IsNullOrWhiteSpace(NewItemName))
                return;

            var newItem = new PackingItemDTO { Name = NewItemName, Category = 3, RemoteTripId = _remoteTripId, LocalTripId = _localTripId, RemoteUserId = Session.UserId, LocalUserId = Session.LocalUserId, CreatedDate = DateTime.Now };

            await _packingItemRepository.AddPackingItemAsync(newItem);

            NewItemName = string.Empty;

            if (!Session.IsAuthenticated)
            {
                await LoadData();
            }
        }

        private async Task ToggleIsPackedAsync(PackingItem packingItem)
        {
            if (packingItem != null)
            {
                try
                {
                    await _packingItemRepository.UpdatePackingItemAsync(Mappers.MapToPackingItemDTO(packingItem));
                }
                catch (Supabase.Postgrest.Exceptions.PostgrestException ex)
                {
                    // Logowanie pełnego wyjątku
                    Console.WriteLine($"Error: {ex.Message}, {ex.StackTrace}");
                }
            }
        }

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
            if (change.Item.RemoteTripId != _remoteTripId)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                switch (change.Type)
                {
                    case PackingItemChangeType.Insert:
                        Items.Add(Mappers.MapToPackingItem(change.Item));
                        break;

                    case PackingItemChangeType.Update:
                        var item = Items.FirstOrDefault(x => x.RemoteId == change.Item.RemotePackingItemId);
                        if (item != null)
                        {
                            var index = Items.IndexOf(item);

                            if (index >= 0)
                            {
                                Items[index] = Mappers.MapToPackingItem(change.Item);
                            }
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
                    Items.ReplaceRange(tripsWithStats.Select(Mappers.MapToPackingItem));
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

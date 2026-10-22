using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Extensions;
using Packo.Interfaces;
using Packo.Messages;
using Packo.Models.DTO;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using Packo.Services;
using Packo.Views;
using System.Windows.Input;

namespace Packo.ViewModels
{
    public partial class TripListViewModel : BaseViewModel
    {
        private readonly ITripListPreloadService _tripListPreloadService;
        private readonly IWeatherPreloadService _weatherPreloadService;

        public ObservableRangeCollection<TripViewModel> Trips { get; } = new();

        //public ICommand TripTappedCommand => new Command<TripViewModel>(OnTripTapped);
        public IAsyncRelayCommand<TripViewModel> TripTappedCommand { get; }

        //
        public ICommand AddTripCommand => new Command(async () => await AddTrip("wycieczka 1 test"));
        public ICommand DeleteTripCommand => new Command<TripViewModel>(async (trip) => await DeleteTripAsync(trip));
        public ICommand TrashTripCommand => new Command<TripViewModel>(TrashTripAsync);
        public IAsyncRelayCommand LogoutCommand => new AsyncRelayCommand(async () => await Logout());

        //public IAsyncRelayCommand LoginWithGoogleCommand { get; }

        public TripListViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, ITripListPreloadService tripListPreloadService, IWeatherPreloadService weatherPreloadService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _tripListPreloadService = tripListPreloadService;
            _weatherPreloadService = weatherPreloadService;

            Title = LocalizationResourceManager.Instance["TabTrips"];
            LocalizationResourceManager.Instance.PropertyChanged += (_, _) => Title = LocalizationResourceManager.Instance["TabTrips"];

            //LoginWithGoogleCommand = new AsyncRelayCommand(LoginWithGoogle, () => !IsBusy);

            TripTappedCommand = new AsyncRelayCommand<TripViewModel>(OnTripTapped, trip => !Session.GlobalIsBusy);


            // Subscribe to login completed message
            WeakReferenceMessenger.Default.Register<LoginCompletedMessage>(this, async (r, m) =>
            {
                await OnLoginCompleted();
            });

            WeakReferenceMessenger.Default.Register<TripDataChangedMessage>(this, (r, m) =>
            {
                _isDirty = true;
                _tripListPreloadService.Invalidate();
                _weatherPreloadService.Invalidate();
            });

            WeakReferenceMessenger.Default.Register<LogoutCompletedMessage>(this, async (r, m) =>
            {
                await OnLogoutCompleted();
                //MainThread.BeginInvokeOnMainThread(() => Trips.Clear());
            });
        }

        private async Task OnLoginCompleted()
        {
            _tripRepository.TripChanged += OnTripChanged;
            _tripListPreloadService.Invalidate();
            await LoadData(forceRefresh: true);
        }

        private async Task OnLogoutCompleted()
        {
            Trips.Clear();
            _isLoaded = false;
            _tripListPreloadService.Invalidate();
            await _tripRepository.DeleteLocalTrips();
        }

        private async Task OnTripTapped(TripViewModel trip)
        {
            if (trip == null)
                return;



            await Shell.Current.GoToAsync(nameof(PackingListPage), new Dictionary<string, object>
            {
                ["remoteTripId"] = trip.TripModel.RemoteTripId,
                ["localTripId"] = trip.TripModel.LocalTripId
            });
        }

        private async void TrashTripAsync(TripViewModel trip)
        {
            //trip.TripModel.IsInTrash = true;

            //await _tripRepository.UpdateTripAsync(trip.TripModel);
            ////await _tripRepository.DeleteTripAsync(trip.TripModel);
            ///

            await _tripRepository.DeleteTripAsync(trip.TripModel);


            ////await LoadData();


            //var updated = trip.TripModel;

            //if (updated != null)
            //{
            //    updated.IsInTrash = true;
            //    await _tripRepository.UpdateTripAsync(updated);
            //}


            //await _tripRepository.UpdateTripAsync(trip.TripModel);
        }

        private async Task DeleteTripAsync(TripViewModel trip)
        {
            try
            {
                await _tripRepository.DeleteTripAsync(trip.TripModel);
            }
            catch (Supabase.Postgrest.Exceptions.PostgrestException ex)
            {
                // Logowanie pełnego wyjątku
                Console.WriteLine($"Error: {ex.Message}, {ex.StackTrace}");
            }
        }

        private async Task AddTrip(string destinationName)
        {
            await Shell.Current.GoToAsync(nameof(TripSetupPage));

            //if (Session.LocalUserId == null)
            //{
            //    var localUser = await _localUserService.CreateLocalUserAsync();

            //    Session.SetLocalUser(localUser.LocalUserId);
            //}

            //await _tripRepository.AddTripAsync(new TripDTO { IsActive = true, Destination = destinationName, CreatedDate = DateTime.Now, RemoteUserId = Session.UserId, LocalUserId = Session.LocalUserId });

            //if (!Session.IsAuthenticated)
            //{
            //    await LoadData();
            //}
        }

        public async Task Logout()
        {
            var popup = new ConfirmPopup(LocalizationResourceManager.Instance["LogoutConfirmTitle"], LocalizationResourceManager.Instance["LogoutConfirmMessage"]);

            var result = await Application.Current.MainPage.ShowPopupAsync<bool>(popup);

            if (result.Result)
            {
                //_tripRepository.TripChanged -= OnTripChanged;
                await _tripRepository.UnsubscribeFromTripChangesAsync();
                await _packingItemRepository.UnsubscribeFromPackingItemChangesAsync();
                await _supabase.Client.Auth.SignOut();
                await Shell.Current.GoToAsync("//Home");
            }
        }

        //private async Task LoginWithGoogle()
        //{
        //    if (IsBusy)
        //        return;

        //    IsBusy = true;
        //    LoginWithGoogleCommand.NotifyCanExecuteChanged();

        //    try
        //    {
        //        var token = await _googleAuthService.SignInWithGoogleAsync();

        //        if (token != null)
        //        {
        //            // Tutaj możesz np. zalogować użytkownika w Supabase:
        //            var session = await _supabase.Client.Auth.SignInWithIdToken(Supabase.Gotrue.Constants.Provider.Google, token);

        //            if (session != null)
        //            {
        //                Session.SetUser(session.User);

        //                //_sessionService.SetUser(session.User);

        //                // Możesz teraz np. ustawić w ViewModel flagę:
        //                //IsLoggedIn = true;
        //                //var LoggedInUserName = user?.Email ?? user?.Id;

        //                //await Shell.Current.GoToAsync(nameof(TripListPage));

        //                await _tripRepository.StartRealtimeAsync();

        //                await _packingItemRepository.StartRealtimeAsync();

        //                await _tripRepository.SyncPendingChangesAsync();

        //                //await LoadData();

        //                var trips = await _tripRepository.GetActiveTripsWithStatsAsync();

        //                foreach (var trip in trips)
        //                {
        //                    await _packingItemRepository.UpdatePendingPackingItems(trip.Trip.LocalTripId, trip.Trip.RemoteTripId);
        //                    await _packingItemRepository.SyncPendingChangesAsync();
        //                }

        //                _tripRepository.TripChanged += OnTripChanged;
        //                //await _packingItemRepository.SyncPendingChangesAsync();
        //            }
        //        }

        //        //await _tripRepository.UnsubscribeFromTripChangesAsync();

        //        //await _packingItemRepository.UnsubscribeFromPackingItemChangesAsync();


        //        await LoadData();
        //    }
        //    catch (Exception ex)
        //    {
        //        // obsługa błędu
        //    }
        //    finally
        //    {
        //        IsBusy = false;
        //        LoginWithGoogleCommand.NotifyCanExecuteChanged();
        //    }
        //}

        public Task DisposeRealtimeAsync()
        {
            if (_subscription != null)
            {
                _subscription.Unsubscribe();
            }

            return Task.CompletedTask;
        }



        private void OnTripChanged(TripDTO trip, string operation)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                switch (operation)
                {
                    case "INSERT":
                        Trips.Add(new TripViewModel(trip));
                        break;
                    case "UPDATE":
                        var existing = Trips.FirstOrDefault(t => t.TripModel.RemoteTripId == trip.RemoteTripId);
                        if (existing != null)
                        {
                            Trips.Remove(existing);
                            existing.UpdateFromTrip(trip);
                        }
                        //
                        break;
                    case "DELETE":
                        var toRemove = Trips.FirstOrDefault(t => t.TripModel.RemoteTripId == trip.RemoteTripId);
                        if (toRemove != null)
                            Trips.Remove(toRemove);
                        break;
                }
            });
        }

        private async Task LoadData(bool forceRefresh = false)
        {
            try
            {
                SetBusy(true, null);

                var tripsWithStats = await _tripListPreloadService.GetTripsAsync(forceRefresh);

                var tripViewModels = new List<TripViewModel>();

                foreach (var item in tripsWithStats)
                {
                    item.Trip.IsActive = item.Trip.EndDate > DateTime.Now;

                    tripViewModels.Add(new TripViewModel(item.Trip)
                    {
                        PackingSummary = item.PackingSummary,
                    });
                }

                var orderedTrips = tripViewModels
                    .OrderByDescending(x => x.TripModel.IsActive)
                    .ThenBy(x => x.TripModel.StartDate);

                Trips.Clear();
                Trips.AddRange(orderedTrips);
                _isLoaded = true;
                _isDirty = false;

            }
            catch (Exception ex)
            {
                // obsługa błędu
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        //protected override async Task OnNavigatedToAsync(IDictionary<string, object> query)
        //{
        //    if (Session.IsAuthenticated && !await _tripRepository.IsChannelCreatedAsync())
        //    {
        //        await _tripRepository.StartRealtimeAsync();
        //    }

        //    _tripRepository.TripChanged -= OnTripChanged;
        //    _tripRepository.TripChanged += OnTripChanged;

        //    await LoadData();
        //}

        private bool _isAppearing;
        private bool _isLoaded;
        private bool _isDirty;

        public bool IsLoadingModalActive => _isAppearing || Session.GlobalIsBusy;

        public async Task OnAppearingAsync()
        {
            if (_isAppearing)
                return;

            _isAppearing = true;

            try
            {
                if (Session.IsAuthenticated)
                {
                    if (!await _tripRepository.IsChannelCreatedAsync())
                    {
                        await _tripRepository.StartRealtimeAsync();
                    }

                    _tripRepository.TripChanged -= OnTripChanged;
                    _tripRepository.TripChanged -= OnTripChangedWhileAway;
                    _tripRepository.TripChanged += OnTripChanged;
                }

                if (!_isLoaded || _isDirty)
                {
                    await LoadData(forceRefresh: _isDirty);
                }
            }
            finally
            {
                _isAppearing = false;
            }
        }

        public override async Task OnNavigatedFromAsync(IDictionary<string, object> query)
        {
            _tripRepository.TripChanged -= OnTripChanged;
            _tripRepository.TripChanged -= OnTripChangedWhileAway;
            _tripRepository.TripChanged += OnTripChangedWhileAway;
            await _tripRepository.SyncPendingChangesAsync();
        }

        private void OnTripChangedWhileAway(TripDTO trip, string operation) => _isDirty = true;
    }
}

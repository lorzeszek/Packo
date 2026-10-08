using CommunityToolkit.Mvvm.Input;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.Views;
using System.Windows.Input;

namespace Packo.ViewModels
{
    public class StartViewModel : BaseViewModel
    {
        public IRelayCommand LoginWithGoogleCommand => new AsyncRelayCommand(LoginWithGoogle);

        public ICommand StartPackingCommand => new AsyncRelayCommand(StartPackingAsync);

        private TripViewModel _trip;
        public TripViewModel Trip
        {
            get => _trip;
            set
            {
                if (_trip != value)
                {
                    _trip = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TripDestination));
                    OnPropertyChanged(nameof(TripDatesRange));
                    OnPropertyChanged(nameof(TripCountdown));
                }
            }
        }

        public string TripDestination => Trip?.Destination ?? string.Empty;
        public string TripDatesRange => Trip?.TripDatesRange ?? string.Empty;
        public string TripCountdown => Trip?.TripCountdown ?? string.Empty;

        private bool _hasActiveTrips;
        public bool HasActiveTrips
        {
            get => _hasActiveTrips;
            set
            {
                if (_hasActiveTrips != value)
                {
                    _hasActiveTrips = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowStartButton));
                }
            }
        }

        private bool _isDataLoaded;
        public bool IsDataLoaded
        {
            get => _isDataLoaded;
            set
            {
                if (_isDataLoaded != value)
                {
                    _isDataLoaded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowStartButton));
                }
            }
        }



        public bool ShowStartButton => IsDataLoaded && !HasActiveTrips;

        public StartViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            //_googleAuthService = googleAuthService;
        }

        //public async Task InitializeAsync()
        //{
        //    await Session.InitializeAsync();

        //    if (Session.IsLoggedIn)
        //    {
        //        await Shell.Current.GoToAsync("//TripList");
        //        return;
        //    }

        //    // jeśli NIE zalogowany → zostajemy na StartPage
        //    // tu będzie AI onboarding
        //}

        private async Task StartPackingAsync()
        {
            // przechodzisz do flow zbierania danych
            await Shell.Current.GoToAsync(nameof(TripSetupPage));
        }

        private async Task LoginWithGoogle()
        {
            try
            {
                var token = await _googleAuthService.SignInWithGoogleAsync();

                if (token != null)
                {
                    // Tutaj możesz np. zalogować użytkownika w Supabase:
                    var session = await _supabase.Client.Auth.SignInWithIdToken(Supabase.Gotrue.Constants.Provider.Google, token);

                    if (session != null)
                    {
                        //_sessionService.SetUser(session.User);

                        // Możesz teraz np. ustawić w ViewModel flagę:
                        //IsLoggedIn = true;
                        //var LoggedInUserName = user?.Email ?? user?.Id;

                        //await Shell.Current.GoToAsync(nameof(TripListPage));

                        await _tripRepository.StartRealtimeAsync();

                        await _packingItemRepository.StartRealtimeAsync();
                    }
                }

                //await _tripRepository.UnsubscribeFromTripChangesAsync();

                //await _packingItemRepository.UnsubscribeFromPackingItemChangesAsync();



            }
            catch (Exception ex)
            {
                // obsługa błędu
            }
        }


        public async Task OnAppearingAsync()
        {
            var tripsWithStats = await _tripRepository.GetActiveTripsWithStatsAsync();
            var nearestTrip = tripsWithStats?.OrderBy(t => t.Trip.StartDate).FirstOrDefault(x => x.Trip.EndDate >= DateTime.Now);

            HasActiveTrips = tripsWithStats != null && tripsWithStats.Any(x => x.Trip.IsActive);

            if (HasActiveTrips && nearestTrip != null)
            {
                Title = nearestTrip.Trip.StartDate < DateTime.Now ? "Current Trip" : "Next Trip";
                //Title = nearestTrip.Trip.StartDate < DateTime.Now ? "Current Trip: " + (nearestTrip?.Trip.Destination) : "Next Trip: " + (nearestTrip?.Trip.Destination);

                Trip = new TripViewModel(nearestTrip.Trip)
                {
                    PackingSummary = nearestTrip.PackingSummary,
                };
            }
            else
            {
                Title = "Ready for your next adventure?";
            }

            IsDataLoaded = true;
        }

        //protected override async Task OnNavigatedToAsync(IDictionary<string, object> query)
        //{
        //    if (Session.IsAuthenticated)
        //    {
        //        var tripsWithStats = await _tripRepository.GetActiveTripsWithStatsAsync();

        //        var nearestTrip = tripsWithStats.OrderBy(t => t.Trip.StartDate).FirstOrDefault();

        //        Title = "Next Trip: " + (nearestTrip?.Trip.Destination ?? "None");
        //    }
        //}
    }
}

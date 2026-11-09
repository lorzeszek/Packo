using CommunityToolkit.Mvvm.Input;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.Services;
using Packo.Views;
using Packo.Models.DTO;
using System.Globalization;

namespace Packo.ViewModels
{
    public class StartViewModel : BaseViewModel
    {
        public const string Route = "TripDetails";
        private readonly ITripListPreloadService _tripListPreloadService;

        private TripDTO? _selectedTrip;

        public IAsyncRelayCommand BackToTripsCommand { get; }
        public IAsyncRelayCommand RetryLoadCommand { get; }
        public IAsyncRelayCommand EditTripCommand { get; }

        private TripViewModel? _trip;
        public TripViewModel? Trip
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
                    OnPropertyChanged(nameof(TripDetailsSummary));
                    EditTripCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string TripDestination => Trip?.Destination ?? string.Empty;
        public string TripDatesRange => Trip?.TripDatesRange ?? string.Empty;
        public string TripCountdown => Trip?.TripCountdown ?? string.Empty;

        public string TripDetailsSummary
        {
            get
            {
                var trip = Trip?.TripModel;
                if (trip == null)
                    return string.Empty;

                var strings = LocalizationResourceManager.Instance;
                var details = new List<string>
                {
                    string.Format(CultureInfo.CurrentCulture,
                        strings[trip.AdultsCount == 1 ? "TripAdultCountOne" : "TripAdultCountMany"], trip.AdultsCount)
                };
                var children = trip.ChildrenAges.Count;
                if (children > 0)
                    details.Add(string.Format(CultureInfo.CurrentCulture,
                        strings[children == 1 ? "TripChildCountOne" : "TripChildCountMany"], children));

                if (trip.StartDate is DateTime start && trip.EndDate is DateTime end && end.Date >= start.Date)
                {
                    var days = (end.Date - start.Date).Days + 1;
                    details.Add(string.Format(CultureInfo.CurrentCulture,
                        strings[days == 1 ? "TripDurationOneDay" : "TripDurationDays"], days));
                }

                return string.Join(" · ", details);
            }
        }

        private bool _hasTrip;
        public bool HasTrip
        {
            get => _hasTrip;
            set
            {
                if (_hasTrip != value)
                {
                    _hasTrip = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowTripUnavailable));
                    OnPropertyChanged(nameof(IsLoadingTrip));
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
                    OnPropertyChanged(nameof(ShowTripUnavailable));
                    OnPropertyChanged(nameof(IsLoadingTrip));
                }
            }
        }



        private bool _hasLoadError;
        public bool HasLoadError
        {
            get => _hasLoadError;
            private set
            {
                if (SetProperty(ref _hasLoadError, value))
                    OnPropertyChanged(nameof(ShowTripUnavailable));
            }
        }

        public bool ShowTripUnavailable => IsDataLoaded && !HasTrip && !HasLoadError;
        public bool IsLoadingTrip => !HasTrip && !IsDataLoaded;

        public StartViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, ITripListPreloadService tripListPreloadService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _tripListPreloadService = tripListPreloadService;
            EditTripCommand = new AsyncRelayCommand(EditTripAsync, () => Trip != null);
            BackToTripsCommand = new AsyncRelayCommand(() => Shell.Current.GoToAsync("//TripList"));
            RetryLoadCommand = new AsyncRelayCommand(() => LoadSelectedTripAsync(forceRefresh: true));
            Title = LocalizationResourceManager.Instance["TripDetails"];
            LocalizationResourceManager.Instance.PropertyChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Title = LocalizationResourceManager.Instance["TripDetails"];
            OnPropertyChanged(nameof(TripDatesRange));
            OnPropertyChanged(nameof(TripCountdown));
            OnPropertyChanged(nameof(TripDetailsSummary));
        }

        protected override Task OnNavigatedToAsync(IDictionary<string, object> query)
        {
            if (query.TryGetValue("trip", out var value) && value is TripDTO trip)
            {
                _selectedTrip = trip;
                Trip = new TripViewModel(trip);
                HasTrip = true;
                HasLoadError = false;
            }

            return Task.CompletedTask;
        }

        private async Task EditTripAsync()
        {
            if (Trip == null || Session.GlobalIsBusy)
                return;

            await Shell.Current.GoToAsync(nameof(TripSetupPage), new ShellNavigationQueryParameters
            {
                ["editTrip"] = Trip.TripModel
            });
        }

        public Task OnAppearingAsync() => LoadSelectedTripAsync();

        public void RefreshTripSchedule()
        {
            Trip?.RefreshSchedule();
            OnPropertyChanged(nameof(TripCountdown));
        }

        private async Task LoadSelectedTripAsync(bool forceRefresh = false)
        {
            try
            {
                HasLoadError = false;
                IsDataLoaded = false;
                var tripsWithStats = await _tripListPreloadService.GetTripsAsync(forceRefresh);
                var selectedTrip = _selectedTrip;
                var match = selectedTrip == null ? null : tripsWithStats.FirstOrDefault(x =>
                    !x.Trip.IsInTrash && (selectedTrip.RemoteTripId > 0
                        ? x.Trip.RemoteTripId == selectedTrip.RemoteTripId
                        : x.Trip.LocalTripId == selectedTrip.LocalTripId
                            && x.Trip.LocalUserId == selectedTrip.LocalUserId));

                var trip = match?.Trip;
                if (trip is null && selectedTrip is { RemoteTripId: 0, LocalTripId: > 0 }
                    && selectedTrip.LocalUserId == Session.LocalUserId)
                    trip = await _tripRepository.GetLocalTripAsync(selectedTrip.LocalTripId, selectedTrip.LocalUserId);

                Trip = trip is { IsInTrash: false } ? new TripViewModel(trip) : null;
                HasTrip = Trip != null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Trip details loading failed: {ex}");
                HasLoadError = true;
            }
            finally
            {
                IsDataLoaded = true;
            }
        }
    }
}

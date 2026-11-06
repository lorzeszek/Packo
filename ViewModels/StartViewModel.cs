using CommunityToolkit.Mvvm.Input;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.Services;
using Packo.Views;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Models.DTO;
using System.Globalization;

namespace Packo.ViewModels
{
    public class StartViewModel : BaseViewModel
    {
        private readonly ITripListPreloadService _tripListPreloadService;

        private TripDTO? _selectedTrip;

        public IAsyncRelayCommand BackToTripsCommand { get; }
        public IAsyncRelayCommand RetryLoadCommand { get; }
        public IAsyncRelayCommand OpenPackingListCommand { get; }
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
                    OnPropertyChanged(nameof(PackingSummary));
                    OnPropertyChanged(nameof(TripDetailsSummary));
                    NotifyPackingProgressChanged();
                    EditTripCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string PackingSummary => Trip?.PackingSummary ?? string.Empty;

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

        // Progress summaries from both repositories and change messages use "packed / total".
        private (int Packed, int Total) PackingCounts
        {
            get
            {
                var counts = PackingSummary.Split('/');
                if (counts.Length == 2
                    && int.TryParse(counts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var packed)
                    && int.TryParse(counts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
                    && total > 0)
                    return (Math.Clamp(packed, 0, total), total);

                return (0, 0);
            }
        }

        public double PackingProgress => PackingCounts.Total > 0
            ? (double)PackingCounts.Packed / PackingCounts.Total : 0;

        public string PackingProgressText => PackingCounts.Total > 0
            ? string.Format(CultureInfo.CurrentCulture, LocalizationResourceManager.Instance["StartPackingCount"],
                PackingCounts.Packed, PackingCounts.Total)
            : LocalizationResourceManager.Instance["StartPackingEmpty"];

        public string PackingProgressPercent => PackingProgress.ToString("P0", CultureInfo.CurrentCulture);

        private void NotifyPackingProgressChanged()
        {
            OnPropertyChanged(nameof(PackingProgress));
            OnPropertyChanged(nameof(PackingProgressText));
            OnPropertyChanged(nameof(PackingProgressPercent));
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

        public StartViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, ITripListPreloadService tripListPreloadService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _tripListPreloadService = tripListPreloadService;
            EditTripCommand = new AsyncRelayCommand(EditTripAsync, () => Trip != null);
            OpenPackingListCommand = new AsyncRelayCommand(OpenPackingListAsync);
            BackToTripsCommand = new AsyncRelayCommand(() => Shell.Current.GoToAsync("//TripList"));
            RetryLoadCommand = new AsyncRelayCommand(() => LoadSelectedTripAsync(forceRefresh: true));
            Title = LocalizationResourceManager.Instance["TripDetails"];
            LocalizationResourceManager.Instance.PropertyChanged += OnLanguageChanged;
            WeakReferenceMessenger.Default.Register<TripDataChangedMessage>(this, (recipient, message) =>
            {
                if (message.PackingSummary == null)
                    return;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    var trip = Trip?.TripModel;
                    if (trip != null && message.LocalUserId == Session.LocalUserId
                        && (message.RemoteTripId > 0 ? trip.RemoteTripId == message.RemoteTripId
                            : trip.LocalTripId == message.LocalTripId && trip.LocalUserId == message.LocalUserId))
                    {
                        Trip!.PackingSummary = message.PackingSummary;
                        OnPropertyChanged(nameof(PackingSummary));
                        NotifyPackingProgressChanged();
                    }
                });
            });
        }

        private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Title = LocalizationResourceManager.Instance["TripDetails"];
            OnPropertyChanged(nameof(TripDatesRange));
            OnPropertyChanged(nameof(TripCountdown));
            OnPropertyChanged(nameof(TripDetailsSummary));
            NotifyPackingProgressChanged();
        }

        protected override Task OnNavigatedToAsync(IDictionary<string, object> query)
        {
            if (query.TryGetValue("selectedTrip", out var value) && value is TripViewModel selectedTrip)
            {
                _selectedTrip = selectedTrip.TripModel;
                Trip = selectedTrip;
                HasTrip = true;
                HasLoadError = false;
            }

            return Task.CompletedTask;
        }

        private async Task OpenPackingListAsync()
        {
            if (Trip == null || Session.GlobalIsBusy)
                return;

            await Shell.Current.GoToAsync(nameof(PackingListPage), new ShellNavigationQueryParameters
            {
                ["trip"] = Trip.TripModel
            });
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

                Trip = match == null ? null : new TripViewModel(match.Trip)
                {
                    PackingSummary = match.PackingSummary
                };
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

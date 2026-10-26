using Packo.Extensions;
using Packo.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Input;
using Packo.Messages;
using Packo.Models.DTO;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;

namespace Packo.ViewModels
{
    public class WeatherViewModel : BaseViewModel
    {
        public ObservableRangeCollection<DestinationWeatherDTO> Destinations { get; } = new ObservableRangeCollection<DestinationWeatherDTO>();

        private readonly IWeatherPreloadService _weatherPreloadService;
        private bool _isLoading;
        private bool _hasLoaded;
        private string _loadErrorMessage = string.Empty;

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (SetProperty(ref _isLoading, value))
                    OnPropertyChanged(nameof(ShowEmptyState));
            }
        }

        public string LoadErrorMessage
        {
            get => _loadErrorMessage;
            private set
            {
                if (SetProperty(ref _loadErrorMessage, value))
                {
                    OnPropertyChanged(nameof(HasLoadError));
                    OnPropertyChanged(nameof(ShowEmptyState));
                }
            }
        }

        public bool HasLoadError => !string.IsNullOrEmpty(LoadErrorMessage);
        public bool HasForecasts => Destinations.Count > 0;
        public bool ShowEmptyState => _hasLoaded && !IsLoading && !HasLoadError && !HasForecasts;
        public IAsyncRelayCommand RetryLoadCommand { get; }

        public WeatherViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, IWeatherPreloadService weatherPreloadService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _weatherPreloadService = weatherPreloadService;
            RetryLoadCommand = new AsyncRelayCommand(() => LoadWeatherAsync(forceRefresh: true));
            Destinations.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasForecasts));
                OnPropertyChanged(nameof(ShowEmptyState));
            };

            WeakReferenceMessenger.Default.Register<LogoutCompletedMessage>(this, (_, _) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _hasLoaded = false;
                    LoadErrorMessage = string.Empty;
                    Destinations.Clear();
                    OnPropertyChanged(nameof(ShowEmptyState));
                });
            });
        }

        public Task OnAppearingAsync() => LoadWeatherAsync();

        private async Task LoadWeatherAsync(bool forceRefresh = false)
        {
            if (IsLoading)
                return;

            // Local trips also have forecasts; weather itself does not require login.
            var userId = Session.UserId;
            var localUserId = Session.LocalUserId;

            IsLoading = true;
            LoadErrorMessage = string.Empty;
            try
            {
                var weather = await _weatherPreloadService.GetWeatherAsync(forceRefresh);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (userId != Session.UserId || localUserId != Session.LocalUserId)
                        return;

                    Destinations.ReplaceRange(weather.Forecasts);
                    if (weather.HasFailures)
                        LoadErrorMessage = string.Format(LocalizationResourceManager.Instance["WeatherDestinationsUnavailable"],
                            string.Join(", ", weather.UnavailableDestinations));
                    _hasLoaded = true;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Weather loading failed: {ex}");
                if (userId == Session.UserId && localUserId == Session.LocalUserId)
                    LoadErrorMessage = LocalizationResourceManager.Instance["WeatherLoadError"];
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}

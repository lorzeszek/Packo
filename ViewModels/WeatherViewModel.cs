using Packo.Extensions;
using Packo.Interfaces;
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

        public WeatherViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, IWeatherPreloadService weatherPreloadService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _weatherPreloadService = weatherPreloadService;
        }

        public async Task OnAppearingAsync()
        {
            if (_isLoading)
                return;

            _isLoading = true;
            try
            {
                var weather = await _weatherPreloadService.GetWeatherAsync();
                await MainThread.InvokeOnMainThreadAsync(() => Destinations.ReplaceRange(weather));
            }
            finally
            {
                _isLoading = false;
            }
        }

        protected override async Task OnNavigatedToAsync(IDictionary<string, object> query)
        {
            //await OnAppearingAsync();
        }
    }
}

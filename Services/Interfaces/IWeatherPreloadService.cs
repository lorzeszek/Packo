using Packo.Models.DTO;

namespace Packo.Services.Interfaces
{
    public interface IWeatherPreloadService
    {
        Task PrefetchAsync();
        Task<WeatherLoadResult> GetWeatherAsync(bool forceRefresh = false);
        void Invalidate();
    }
}

using Packo.Models.DTO;

namespace Packo.Services.Interfaces
{
    public interface IWeatherPreloadService
    {
        Task PrefetchAsync();
        Task<IReadOnlyList<DestinationWeatherDTO>> GetWeatherAsync(bool forceRefresh = false);
        void Invalidate();
    }
}

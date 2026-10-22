using Packo.Repositories.Models;

namespace Packo.Services.Interfaces
{
    /// <summary>
    /// Shares the initial trips request between app startup and the Trips tab.
    /// </summary>
    public interface ITripListPreloadService
    {
        Task PrefetchAsync();
        Task<IReadOnlyList<TripWithStats>> GetTripsAsync(bool forceRefresh = false);
        void Invalidate();
    }
}

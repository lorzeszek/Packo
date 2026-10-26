using Packo.Models.DTO;
using Packo.Repositories.Enums;

namespace Packo.Repositories.Interfaces
{
    public interface IPackingItemRepository
    {
        Task<IReadOnlyList<PackingItemDTO>> GetPackingItemsForTripAsync(int localTripId, int remoteTripId);
        Task<int> AddPackingItemAsync(PackingItemDTO item);
        Task AddPackingItemsAsync(List<PackingItemDTO> items);
        Task UpdatePackingItemAsync(PackingItemDTO item);
        Task DeletePackingItemAsync(PackingItemDTO item);
        Task DeletePackingItemsAsync(List<PackingItemDTO> items);
        Task UnsubscribeFromPackingItemChangesAsync();
        Task SyncPendingChangesAsync();
        Task StartRealtimeAsync();
        Task<bool> IsChannelCreatedAsync();
        Task UpdatePendingPackingItems(int localTripId, int remoteTripId);

        event Action<PackingItemChange>? PackingItemChanged;
    }
}

using Packo.Models.DTO;

namespace Packo.Repositories.Interfaces;

public interface IPackingListRepository
{
    Task RefreshPackingTripAsync(TripDTO trip);
    Task<IReadOnlyList<PackingListDTO>> GetPackingListsAsync(TripDTO trip, bool refresh = true);
    Task<IReadOnlyList<PackingItemDTO>> GetPackingListItemsAsync(TripDTO trip, Guid listId, bool refresh = true);
    Task<IReadOnlyList<PackingItemDTO>> GetPackingListItemsAsync(TripDTO trip, IReadOnlyCollection<Guid> listIds, bool refresh = true);
    Task<PackingListDTO> CreatePackingListAsync(TripDTO trip, string name);
    Task RenamePackingListAsync(TripDTO trip, Guid listId, string name);
    Task DeletePackingListAsync(TripDTO trip, Guid listId);
}

public interface IPackingListRemote
{
    Task<IReadOnlyList<PackingListDTO>> GetPackingListsAsync(int remoteTripId);
    Task SavePackingListAsync(int remoteTripId, PackingListDTO list, bool create);
    Task DeletePackingListAsync(int remoteTripId, Guid listId);
}

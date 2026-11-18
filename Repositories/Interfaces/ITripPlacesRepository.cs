using Packo.Models;
using Packo.Models.DTO;

namespace Packo.Repositories.Interfaces;

public interface ITripPlacesRepository
{
    Task<TripPlacesState> LoadAsync(TripDTO trip);
    Task<TripPlacesState> ReplaceSuggestionsAsync(TripDTO trip, string contextKey, IReadOnlyList<TripPlace> places);
    Task<TripPlacesState> SaveAsync(TripDTO trip, TripPlace place);
    Task<TripPlacesState> RemoveAsync(TripDTO trip, string placeId);
    Task<TripPlacesState> SetVisitedAsync(TripDTO trip, string placeId, bool visited);
}

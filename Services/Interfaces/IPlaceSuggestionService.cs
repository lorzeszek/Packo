using Packo.Models;
using Packo.Models.DTO;

namespace Packo.Services.Interfaces;

public interface IPlaceSuggestionService
{
    Task<IReadOnlyList<TripPlace>> FindAsync(TripDTO trip, string language,
        IReadOnlyCollection<string> excludedIds, CancellationToken cancellationToken);
}

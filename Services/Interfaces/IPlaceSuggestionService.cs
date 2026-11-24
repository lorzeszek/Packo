using Packo.Models;
using Packo.Models.DTO;

namespace Packo.Services.Interfaces;

public interface IPlaceSuggestionService
{
    bool RequiresSignIn => false;
    Task<IReadOnlyList<TripPlace>> FindAsync(TripDTO trip, string language,
        IReadOnlyCollection<string> excludedIds, CancellationToken cancellationToken);
    Task<TripPlaceDetails> GetDetailsAsync(string placeId, string language, CancellationToken token) =>
        throw new NotSupportedException();
    Task<Uri?> GetWebsiteAsync(string placeId, CancellationToken token) => throw new NotSupportedException();
    Task<byte[]?> GetPhotoAsync(string photoReference, CancellationToken token) => throw new NotSupportedException();
}

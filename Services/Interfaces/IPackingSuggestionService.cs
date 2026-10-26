namespace Packo.Services.Interfaces
{
    public interface IPackingSuggestionService
    {
        Task<List<Packo.Models.DTO.PackingSuggestion>> GenerateItemsAsync(string destination, DateTime start, DateTime end, int adultsCount, List<int> childrenCountWithAge, List<string> selectedTransportTypes, Packo.Models.Enums.TripType tripType, string languageCode);
    }
}

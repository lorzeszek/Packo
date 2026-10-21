using Packo.Services.Interfaces;

namespace Packo.Services
{
    public class PackingSuggestionService : IPackingSuggestionService
    {
        private readonly IAIService _aiService;

        public PackingSuggestionService(IAIService aiService)
        {
            _aiService = aiService;
        }

        public async Task<List<string>> GenerateItemsAsync(string destination, DateTime start, DateTime end, int adultsCount, List<int> childrenAge, List<string> selectedTransportTypes, string languageCode)
        {
            var days = (end - start).Days;
            var outputLanguage = languageCode.Equals("pl", StringComparison.OrdinalIgnoreCase) ? "Polish" : "English";

            var prompt = $@"
                Generate a packing list for a trip.

                Write every item name in {outputLanguage}. Do not mix languages, add explanations, or include a heading.

                Destination: {destination}
                Duration: {days} days
                Adults: {adultsCount}
                Children: {string.Join(", ", childrenAge.Select((age, index) => $"Child {index + 1}: Age {age}"))}
                Transport: {string.Join(", ", selectedTransportTypes)}

                Return ONLY a simple list of item names, one per line, with no numbering or bullets.
                ";

            var response = await _aiService.GetCompletionAsync(prompt);

            return Parse(response);
        }

        private List<string> Parse(string response)
        {
            return response
                .Split('\n')
                .Select(x => x.Trim('-', ' ', '\r'))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }
    }
}

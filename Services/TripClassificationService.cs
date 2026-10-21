using Newtonsoft.Json;
using Packo.Services.Interfaces;
using Packo.Services.ResponseObjects;

namespace Packo.Services
{
    public class TripClassificationService : ITripClassificationService
    {
        private readonly IAIService _aiService;

        public TripClassificationService(IAIService aiService)
        {
            _aiService = aiService;
        }

        public async Task<TripClassificationData> ClassifyTripAsync(string destination, DateTime start, DateTime end, List<string> selectedTransportTypes)
        {
            var days = (end - start).Days;

            var prompt = $@"
                Classify the trip and select the most appropriate cover theme.

                Destination: {destination}
                Duration: {days} days
                Transport: {string.Join(", ", selectedTransportTypes)}

                Available cover themes:

                - big_city
                - business_trip
                - camping
                - mountains_summer_01
                - mountains_summer_02
                - mountains_winter
                - road_trip
                - summer_beach_01
                - summer_beach_02
                - weekend_city_break

                Rules:

                - Return ONLY one cover theme from the list above.
                - business trips -> business_trip
                - mountain trips in winter -> mountains_winter
                - mountain trips in summer -> mountains_summer_01 or mountains_summer_02
                - beach holidays -> summer_beach_01 or summer_beach_02
                - road journeys focused on driving -> road_trip
                - camping trips -> camping
                - short city tourism trips -> weekend_city_break
                - large metropolitan destinations -> big_city

                Return JSON only:

                {{
                    ""coverTheme"": ""theme_name""
                }}
                ";

            var response = await _aiService.GetCompletionAsync(prompt);

            return Parse(response);
        }

        private TripClassificationData Parse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                throw new InvalidOperationException("The trip classification service returned an empty response.");

            // The model may wrap its JSON in prose or a Markdown code block. Extract
            // the object before deserializing so harmless formatting does not stop
            // trip creation before the confirmation popup is shown.
            var objectStart = response.IndexOf('{');
            var objectEnd = response.LastIndexOf('}');
            if (objectStart >= 0 && objectEnd > objectStart)
            {
                var json = response.Substring(objectStart, objectEnd - objectStart + 1);
                try
                {
                    var parsed = JsonConvert.DeserializeObject<TripClassificationData>(json);
                    if (!string.IsNullOrWhiteSpace(parsed?.CoverTheme))
                        return parsed;
                }
                catch (JsonException)
                {
                    // Try the known theme names in the response below.
                }
            }

            var themes = new[]
            {
                "big_city", "business_trip", "camping", "mountains_summer_01",
                "mountains_summer_02", "mountains_winter", "road_trip",
                "summer_beach_01", "summer_beach_02", "weekend_city_break"
            };

            var theme = themes.FirstOrDefault(candidate =>
                response.Contains(candidate, StringComparison.OrdinalIgnoreCase));

            if (theme != null)
                return new TripClassificationData { CoverTheme = theme };

            throw new InvalidOperationException(
                $"The trip classification response did not contain a recognized cover theme. Response: {response}");
        }
    }
}

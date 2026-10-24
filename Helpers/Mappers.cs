using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.Enums;
using Packo.Models.Supabase;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Packo.Helpers
{
    public static class Mappers
    {
        public static TripDTO MapToTripDTO(TripSupabase trip)
        {
            return new TripDTO()
            {
                RemoteTripId = trip.Id,
                LocalTripId = trip.LocalTripId,
                RemoteUserId = trip.User_id,
                LocalUserId = trip.ClientId,
                IsActive = trip.EndDate > DateTime.Now,
                Destination = trip.Destination,
                CreatedDate = trip.CreatedDate,
                ModifiedDate = null,
                StartDate = trip.StartDate.HasValue ? DateTime.SpecifyKind(trip.StartDate.Value, DateTimeKind.Unspecified) : null,
                EndDate = trip.EndDate.HasValue ? DateTime.SpecifyKind(trip.EndDate.Value, DateTimeKind.Unspecified) : null,
                AdultsCount = trip.AdultsCount,
                ChildrenAges = ParseIntegerArray(trip.ChildrenAges),
                SelectedTransportTypes = ParseTextArray(trip.SelectedTransportTypes),
                TripType = Enum.TryParse<TripType>(trip.TripType, true, out var tripType) ? tripType : TripType.Leisure,
                IsInTrash = trip.IsInTrash,
                CoverTheme = Enum.TryParse<CoverThemeType>(trip.CoverTheme, true, out var parsedTheme) ? parsedTheme : CoverThemeType.big_city
            };
        }

        public static TripSupabase MapToTripSupabase(TripDTO trip)
        {
            return new TripSupabase()
            {
                Id = trip.RemoteTripId,
                LocalTripId = trip.LocalTripId,
                User_id = trip.RemoteUserId,
                ClientId = trip.LocalUserId,
                IsActive = trip.IsActive,
                Destination = trip.Destination,
                CreatedDate = trip.CreatedDate,
                ModifiedDate = null,
                StartDate = trip.StartDate.HasValue ? DateTime.SpecifyKind(trip.StartDate.Value, DateTimeKind.Utc) : null,
                EndDate = trip.EndDate.HasValue ? DateTime.SpecifyKind(trip.EndDate.Value, DateTimeKind.Utc) : null,
                AdultsCount = trip.AdultsCount,
                ChildrenAges = JArray.FromObject(trip.ChildrenAges),
                SelectedTransportTypes = JArray.FromObject(trip.SelectedTransportTypes),
                TripType = trip.TripType.ToString(),
                IsInTrash = trip.IsInTrash,
                CoverTheme = trip.CoverTheme.ToString()
            };
        }

        public static PackingItemSupabase MapToPackingItemSupabase(PackingItemDTO item)
        {
            return new PackingItemSupabase()
            {
                Id = item.RemotePackingItemId ?? 0,
                TripId = item.RemoteTripId.Value,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                ModifiedDate = item.ModifiedDate,
                User_id = item.RemoteUserId,
                IsPacked = item.IsPacked,
                Category = item.Category
            };
        }

        public static PackingItemDTO MapToPackingItemDTO(PackingItemSupabase item)
        {
            return new PackingItemDTO()
            {
                RemotePackingItemId = item.Id,
                RemoteTripId = item.TripId,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                ModifiedDate = item.ModifiedDate,
                RemoteUserId = item.User_id,
                IsPacked = item.IsPacked,
                Category = item.Category
            };
        }

        public static PackingItemDTO MapToPackingItemDTO(PackingItem item)
        {
            return new PackingItemDTO()
            {
                LocalPackingItemId = item.LocalId,
                RemotePackingItemId = item.RemoteId,
                RemoteTripId = item.RemoteTripId,
                LocalTripId = item.LocalTripId,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                ModifiedDate = item.ModifiedDate,
                RemoteUserId = item.RemoteUserId,
                LocalUserId = item.LocalUserId,
                IsPacked = item.IsPacked,
                Category = item.Category
            };
        }

        public static PackingItem MapToPackingItem(PackingItemDTO item)
        {
            return new PackingItem()
            {
                LocalId = item.LocalPackingItemId,
                RemoteId = item.RemotePackingItemId,
                RemoteTripId = item.RemoteTripId,
                LocalTripId = item.LocalTripId,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                ModifiedDate = item.ModifiedDate,
                RemoteUserId = item.RemoteUserId,
                LocalUserId = item.LocalUserId,
                IsPacked = item.IsPacked,
                Category = item.Category
            };
        }

        private static List<int> ParseIntegerArray(JToken? value) =>
            ParseTextArray(value)
                .Select(item => int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var age) ? age : (int?)null)
                .Where(age => age.HasValue)
                .Select(age => age!.Value)
                .ToList();

        private static List<string> ParseTextArray(JToken? value)
        {
            if (value is JArray array)
                return array.Values<string>().Where(item => !string.IsNullOrWhiteSpace(item)).ToList()!;

            var serializedValue = value?.Value<string>();
            if (string.IsNullOrWhiteSpace(serializedValue) || serializedValue == "{}")
                return [];

            var trimmed = serializedValue.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    return JArray.Parse(trimmed).Values<string>().Where(item => !string.IsNullOrWhiteSpace(item)).ToList()!;
                }
                catch
                {
                    return [];
                }
            }

            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
                trimmed = trimmed[1..^1];

            return trimmed
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\"))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }

    }
}

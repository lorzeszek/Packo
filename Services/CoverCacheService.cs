using Packo.Helpers;
using Packo.Models.DTO;
using Packo.Services.Interfaces;
using SkiaSharp;

namespace Packo.Services
{
    public class CoverCacheService : ICoverCacheService
    {
        private const int CoverCacheVersion = 2;

        public string GetOrCreateCover(TripDTO trip, string packingSummary)
        {
            var path = GetCoverPath(trip, packingSummary);

            if (File.Exists(path))
                return path;

            DeleteOldCovers(trip);

            var renderer = new TripCoverRenderer();
            using var bitmap = renderer.Render(trip, packingSummary);

            SaveBitmap(bitmap, path);

            return path;
        }

        private static void DeleteOldCovers(TripDTO trip)
        {
            var prefix = $"trip_cover_v{CoverCacheVersion}_{trip.LocalTripId}_";

            foreach (var file in Directory.GetFiles(FileSystem.AppDataDirectory, prefix + "*.png"))
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        private static string GetCoverPath(TripDTO trip, string packingSummary)
        {
            var summaryKey = MakeSafeFileName(packingSummary).Replace("/", "-");

            var destination = MakeSafeFileName(trip.Destination);

            var fileName =
                $"trip_cover_v{CoverCacheVersion}_" +
                $"{trip.LocalTripId}_" +
                $"{destination}_" +
                //$"{trip.CoverTheme}_" +
                $"{trip.StartDate:yyyyMMdd}_" +
                $"{trip.EndDate:yyyyMMdd}_" +
                $"{summaryKey}.png";

            return Path.Combine(FileSystem.AppDataDirectory, fileName);
        }

        private static string MakeSafeFileName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";

            var invalidChars = Path.GetInvalidFileNameChars();

            return new string(
                value
                    .Where(character => !invalidChars.Contains(character))
                    .ToArray())
                .Replace(' ', '_')
                .ToLowerInvariant();
        }

        private static void SaveBitmap(SKBitmap bitmap, string path)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);

            using var stream = File.Create(path);

            data.SaveTo(stream);
        }
    }
}

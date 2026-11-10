using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Models.Supabase;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using SQLite;
using System.Text.Json;

namespace Packo.Repositories.Local
{
    public class LocalTripRepository : ITripRepository
    {
        private readonly SQLiteAsyncConnection _db;
        private readonly ISessionService _sessionService;


        public event Action<TripDTO, string>? TripChanged;

        public LocalTripRepository(SQLiteAsyncConnection db, ISessionService sessionService)
        {
            _db = db;
            _sessionService = sessionService;
        }

        public Task UnsubscribeFromTripChangesAsync() => Task.CompletedTask;

        public async Task<int> AddTripAsync(TripDTO trip)
        {
            var localTrip = ToRow(trip);

            await _db.InsertAsync(localTrip);

            return localTrip.LocalTripId;
        }

        public async Task DeleteTripAsync(TripDTO trip)
        {
            var localTrip = ToRow(trip);

            await _db.DeleteAsync(localTrip);
        }

        public async Task<IReadOnlyList<TripWithStats>> GetActiveTripsWithStatsAsync()
        {
            var sqliteTrips = await _db.Table<SQLiteTrip>()
                .Where(x => x.LocalUserId == _sessionService.LocalUserId && !x.IsInTrash)
                .ToListAsync();
            var sqlitePackingItems = await _db.Table<SQLitePackingItem>().Where(x => x.LocalUserId == _sessionService.LocalUserId && !x.IsStaleCache).ToListAsync();

            var trips = sqliteTrips.Select(ToTrip);

            return trips.Select(tripDTO =>
            {
                var stat = new TripItemsStatsSupabase
                {
                    IsNotPackedCount = sqlitePackingItems.Count(x => x.LocalTripId == tripDTO.LocalTripId && !x.IsPacked
                        && (x.RemoteTripId is not > 0 || x.RemoteTripId == tripDTO.RemoteTripId)),
                    IsPackedCount = sqlitePackingItems.Count(x => x.LocalTripId == tripDTO.LocalTripId && x.IsPacked
                        && (x.RemoteTripId is not > 0 || x.RemoteTripId == tripDTO.RemoteTripId))
                };
                var summary = (stat.IsPackedCount + stat.IsNotPackedCount) == 0
                    ? "0 / 0"
                    : $"{stat.IsPackedCount} / {stat.IsPackedCount + stat.IsNotPackedCount}";
                return new TripWithStats(tripDTO, summary);
            }).ToList();

        }

        public async Task<IReadOnlyList<string>> GetActiveDestinationsAsync()
        {
            var sqliteTrips = await _db.Table<SQLiteTrip>()
                .Where(x => x.LocalUserId == _sessionService.LocalUserId && !x.IsInTrash)
                .ToListAsync();

            var now = DateTime.Now;
            return sqliteTrips
                .Where(x => x.EndDate.HasValue && x.EndDate.Value > now)
                .Where(x => !string.IsNullOrWhiteSpace(x.Destination))
                .OrderByDescending(x => x.StartDate.HasValue && x.StartDate.Value <= now)
                .ThenBy(x => x.StartDate ?? DateTime.MaxValue)
                .Select(x => x.Destination)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<TripDTO?> GetTripAsync(TripDTO trip)
        {
            var sqliteTrip = await _db.Table<SQLiteTrip>().FirstOrDefaultAsync(x => x.LocalUserId == trip.LocalUserId && x.LocalTripId == trip.LocalTripId);

            if (sqliteTrip is null)
                return null;

            return ToTrip(sqliteTrip);
        }

        public async Task UpdateTripAsync(TripDTO trip)
        {
            var localTrip = ToRow(trip);

            await _db.UpdateAsync(localTrip);
        }

        internal static SQLiteTrip ToRow(TripDTO trip) => new()
        {
            LocalTripId = trip.LocalTripId,
            RemoteTripId = trip.RemoteTripId == 0 ? null : trip.RemoteTripId,
            LocalUserId = trip.LocalUserId,
            RemoteUserId = trip.RemoteUserId,
            IsJoinedTrip = trip.IsJoinedTrip,
            ParticipantsJson = JsonSerializer.Serialize(trip.Participants),
            CreatedDate = trip.CreatedDate,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            Destination = trip.Destination,
            AdultsCount = trip.AdultsCount,
            ChildrenAgesJson = JsonSerializer.Serialize(trip.ChildrenAges),
            SelectedTransportTypesJson = JsonSerializer.Serialize(trip.SelectedTransportTypes),
            TripType = trip.TripType.ToString(),
            IsActive = trip.IsActive,
            IsInTrash = trip.IsInTrash,
            CoverTheme = trip.CoverTheme
        };

        private static TripDTO ToTrip(SQLiteTrip row) => new()
        {
            LocalTripId = row.LocalTripId,
            RemoteTripId = row.RemoteTripId ?? 0,
            LocalUserId = row.LocalUserId,
            RemoteUserId = row.RemoteUserId ?? string.Empty,
            IsJoinedTrip = row.IsJoinedTrip,
            Participants = DeserializeOrEmpty<TripParticipant>(row.ParticipantsJson ?? "[]"),
            CreatedDate = row.CreatedDate,
            StartDate = row.StartDate,
            EndDate = row.EndDate,
            Destination = row.Destination,
            AdultsCount = row.AdultsCount,
            ChildrenAges = DeserializeOrEmpty<int>(row.ChildrenAgesJson),
            SelectedTransportTypes = DeserializeOrEmpty<string>(row.SelectedTransportTypesJson),
            TripType = ParseTripType(row.TripType),
            IsActive = row.IsActive,
            IsInTrash = row.IsInTrash,
            CoverTheme = row.CoverTheme
        };

        public Task SyncPendingChangesAsync()
        {
            return Task.CompletedTask;
        }

        public Task StartRealtimeAsync()
        {
            return Task.CompletedTask;
        }

        public Task<bool> IsChannelCreatedAsync()
        {
            return (Task<bool>)Task.CompletedTask;
        }

        public async Task<TripDTO?> GetLocalTripAsync(int localTripId, string localUserId)
        {
            //var sqliteTripall = await _db.Table<SQLiteTrip>().FirstOrDefaultAsync(x => x.LocalUserId == localUserId);
            var sqliteTrip = await _db.Table<SQLiteTrip>().FirstOrDefaultAsync(x => x.LocalUserId == localUserId && x.LocalTripId == localTripId);

            if (sqliteTrip is null)
                return null;

            return ToTrip(sqliteTrip);
        }

        public async Task DeleteLocalTrips()
        {
            //var sqliteTrips = await _db.Table<SQLiteTrip>().Where(x => x.LocalUserId == _sessionService.LocalUserId).ToListAsync();

            await _db.DeleteAllAsync<SQLiteTrip>();
        }

        //public async Task<List<SQLiteTripDocument>> GetByTripIdAsync(Guid tripId)
        public async Task<List<SQLiteTripDocument>> GetByTripDocsAsync()
        {
            return await _db.Table<SQLiteTripDocument>()
                //.Where(x => x.TripId == tripId)
                .ToListAsync();
        }

        public async Task SaveDocAsync(TripDocumentDTO document)
        {
            var item = new SQLiteTripDocument()
            {
                //TripId = document.TripId,
                Id = document.Id,
                FileName = document.FileName,
                AddedAt = document.AddedAt,
                LocalPath = document.LocalPath
            };

            await _db.InsertAsync(item);
        }

        public async Task DeleteDocAsync(Guid id)
        {
            await _db.DeleteAsync<SQLiteTripDocument>(id);
        }

        private static List<T> DeserializeOrEmpty<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return [];

            try
            {
                return JsonSerializer.Deserialize<List<T>>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static Packo.Models.Enums.TripType ParseTripType(string? value) =>
            Enum.TryParse<Packo.Models.Enums.TripType>(value, true, out var tripType)
                ? tripType
                : Packo.Models.Enums.TripType.Leisure;
    }
}

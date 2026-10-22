using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Models.Supabase;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using SQLite;

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
            var localTrip = new SQLiteTrip()
            {
                RemoteTripId = trip.RemoteTripId == 0 ? null : trip.RemoteTripId,
                LocalUserId = trip.LocalUserId.ToString(),
                CreatedDate = trip.CreatedDate,
                ModifiedDate = trip.ModifiedDate,
                StartDate = trip.StartDate,
                EndDate = trip.EndDate,
                Destination = trip.Destination,
                IsActive = trip.IsActive,
                IsInTrash = trip.IsInTrash,
                CoverTheme = trip.CoverTheme,
                //CoverImagePath = trip.CoverImagePath,  we do not save this path on DB
            };

            await _db.InsertAsync(localTrip);

            return localTrip.LocalTripId;
        }

        public async Task DeleteTripAsync(TripDTO trip)
        {
            var localTrip = new SQLiteTrip()
            {
                LocalTripId = trip.LocalTripId,
                RemoteTripId = trip.RemoteTripId == 0 ? null : trip.RemoteTripId,
                LocalUserId = trip.LocalUserId.ToString(),
                CreatedDate = trip.CreatedDate,
                ModifiedDate = trip.ModifiedDate,
                StartDate = trip.StartDate,
                EndDate = trip.EndDate,
                Destination = trip.Destination,
                IsActive = trip.IsActive,
                IsInTrash = trip.IsInTrash,
                CoverTheme = trip.CoverTheme,
                //CoverImagePath = trip.CoverImagePath,  we do not save this path on DB
            };

            await _db.DeleteAsync(localTrip);
        }

        public async Task<IReadOnlyList<TripWithStats>> GetActiveTripsWithStatsAsync()
        {
            var sqliteTrips = await _db.Table<SQLiteTrip>().Where(x => x.LocalUserId == _sessionService.LocalUserId).ToListAsync();
            var sqlitePackingItems = await _db.Table<SQLitePackingItem>().Where(x => x.LocalUserId == _sessionService.LocalUserId).ToListAsync();

            var trips = sqliteTrips.Select(x => new TripDTO
            {
                RemoteTripId = x.RemoteTripId ?? 0,
                LocalTripId = x.LocalTripId,
                LocalUserId = x.LocalUserId,
                CreatedDate = x.CreatedDate,
                ModifiedDate = x.ModifiedDate,
                StartDate = x.StartDate,
                EndDate = x.EndDate,
                Destination = x.Destination,
                IsActive = x.IsActive,
                IsInTrash = x.IsInTrash,
                CoverTheme = x.CoverTheme,
                //CoverImagePath = x.CoverImagePath,  we do not save this path on DB
            });

            return trips.Select(tripDTO =>
            {
                var stat = new TripItemsStatsSupabase
                {
                    IsNotPackedCount = sqlitePackingItems.Where(x => x.LocalTripId == tripDTO.LocalTripId && !x.IsPacked).Count(),
                    IsPackedCount = sqlitePackingItems.Where(x => x.LocalTripId == tripDTO.LocalTripId && x.IsPacked).Count()
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

            var today = DateTime.Today;
            return sqliteTrips
                .Where(x => x.EndDate.HasValue && x.EndDate.Value.Date >= today)
                .Where(x => !string.IsNullOrWhiteSpace(x.Destination))
                .OrderByDescending(x => x.StartDate.HasValue && x.StartDate.Value.Date <= today)
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

            return new TripDTO
            {
                LocalUserId = sqliteTrip.LocalUserId,
                RemoteTripId = sqliteTrip.RemoteTripId ?? 0,
                LocalTripId = sqliteTrip.LocalTripId,
                CreatedDate = sqliteTrip.CreatedDate,
                ModifiedDate = sqliteTrip.ModifiedDate,
                StartDate = sqliteTrip.StartDate,
                EndDate = sqliteTrip.EndDate,
                Destination = sqliteTrip.Destination,
                IsActive = sqliteTrip.IsActive,
                IsInTrash = sqliteTrip.IsInTrash,
                CoverTheme = sqliteTrip.CoverTheme,
                //CoverImagePath = sqliteTrip.CoverImagePath,  we do not save this path on DB
            };
        }

        public async Task UpdateTripAsync(TripDTO trip)
        {
            var localTrip = new SQLiteTrip()
            {
                LocalTripId = trip.LocalTripId,
                LocalUserId = trip.LocalUserId,
                RemoteTripId = trip.RemoteTripId == 0 ? null : trip.RemoteTripId,
                CreatedDate = trip.CreatedDate,
                ModifiedDate = trip.ModifiedDate,
                StartDate = trip.StartDate,
                EndDate = trip.EndDate,
                Destination = trip.Destination,
                IsActive = trip.IsActive,
                IsInTrash = trip.IsInTrash,
                CoverTheme = trip.CoverTheme,
                //CoverImagePath = trip.CoverImagePath,  we do not save this path on DB
            };

            await _db.UpdateAsync(localTrip);
        }

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

            return new TripDTO
            {
                LocalUserId = sqliteTrip.LocalUserId,
                RemoteTripId = sqliteTrip.RemoteTripId ?? 0,
                LocalTripId = sqliteTrip.LocalTripId,
                CreatedDate = sqliteTrip.CreatedDate,
                ModifiedDate = sqliteTrip.ModifiedDate,
                StartDate = sqliteTrip.StartDate,
                EndDate = sqliteTrip.EndDate,
                Destination = sqliteTrip.Destination,
                IsActive = sqliteTrip.IsActive,
                IsInTrash = sqliteTrip.IsInTrash,
                CoverTheme = sqliteTrip.CoverTheme,
                //CoverImagePath = sqliteTrip.CoverImagePath,  we do not save this path on DB
            };
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
    }
}

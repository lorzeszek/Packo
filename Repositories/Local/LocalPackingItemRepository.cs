using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Enums;
using Packo.Repositories.Interfaces;
using SQLite;

namespace Packo.Repositories.Local
{
    public class LocalPackingItemRepository : IPackingItemRepository
    {
        private readonly SQLiteAsyncConnection _db;

        public event Action<PackingItemChange>? PackingItemChanged;

        public LocalPackingItemRepository(SQLiteAsyncConnection db)
        {
            _db = db;
        }

        public static async Task EnsureSchemaAsync(SQLiteAsyncConnection db)
        {
            await db.CreateTableAsync<SQLitePackingItem>();
            var columns = await db.GetTableInfoAsync("SQLitePackingItem");
            if (!columns.Any(column => column.Name == nameof(SQLitePackingItem.IsStaleCache)))
                await db.ExecuteAsync("ALTER TABLE SQLitePackingItem ADD COLUMN IsStaleCache INTEGER NOT NULL DEFAULT 0");
            // sqlite-net may auto-add the column as nullable when upgrading an existing table.
            await db.ExecuteAsync("UPDATE SQLitePackingItem SET IsStaleCache = 0 WHERE IsStaleCache IS NULL");
            if (!columns.Any(column => column.Name == nameof(SQLitePackingItem.Quantity)))
                await db.ExecuteAsync("ALTER TABLE SQLitePackingItem ADD COLUMN Quantity INTEGER NOT NULL DEFAULT 1");
            await db.ExecuteAsync("UPDATE SQLitePackingItem SET Quantity = 1 WHERE Quantity IS NULL OR Quantity < 1");
            await db.CreateTableAsync<SQLitePackingList>();
            await db.RunInTransactionAsync(connection =>
            {
                // Legacy items keep a stable list identity, including on devices without a connection.
                connection.Execute("""
                    INSERT INTO SQLitePackingList (Id, LocalUserId, LocalTripId, RemoteTripId, Operation)
                    SELECT ?, i.LocalUserId, i.LocalTripId, COALESCE(i.RemoteTripId, 0),
                           CASE WHEN COALESCE(i.RemoteTripId, 0) = 0 THEN ? ELSE 0 END
                    FROM SQLitePackingItem i
                    WHERE i.PackingListId IS NULL AND i.IsStaleCache = 0 AND i.LocalUserId IS NOT NULL
                      AND (COALESCE(i.RemoteTripId, 0) > 0 OR NOT EXISTS (
                          SELECT 1 FROM SQLitePackingItem synced
                          WHERE synced.LocalUserId = i.LocalUserId AND synced.LocalTripId = i.LocalTripId
                            AND synced.RemoteTripId > 0 AND synced.PackingListId IS NULL AND synced.IsStaleCache = 0
                      ))
                      AND NOT EXISTS (
                          SELECT 1 FROM SQLitePackingList l WHERE l.Id = ? AND l.LocalUserId = i.LocalUserId
                            AND l.LocalTripId = i.LocalTripId
                            AND (l.RemoteTripId = COALESCE(i.RemoteTripId, 0)
                                 OR l.RemoteTripId = 0 OR COALESCE(i.RemoteTripId, 0) = 0)
                      )
                    GROUP BY i.LocalUserId, i.LocalTripId, COALESCE(i.RemoteTripId, 0)
                    """, Guid.Empty, (int)PackingListOperation.Create, Guid.Empty);
                connection.Execute("UPDATE SQLitePackingItem SET PackingListId = ? WHERE PackingListId IS NULL", Guid.Empty);
            });
        }

        public async Task<int> AddPackingItemAsync(PackingItemDTO item)
        {
            var localPackingItem = new SQLitePackingItem()
            {
                LocalPackingItemId = item.LocalPackingItemId,
                LocalUserId = item.LocalUserId.ToString(),
                LocalTripId = item.LocalTripId,
                RemotePackingItemId = item.RemotePackingItemId,
                RemoteTripId = item.RemoteTripId,
                PackingListId = item.PackingListId,
                RemoteUserId = item.RemoteUserId,
                //ClientId = item..ToString(),
                //SupabaseItemId = item.Id,
                //TripId = item.TripId,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                //User_id = item.User_id,
                IsPacked = item.IsPacked,
                Quantity = Math.Max(1, item.Quantity),
                NameVersion = item.NameVersion,
                QuantityVersion = item.QuantityVersion,
                IsPackedVersion = item.IsPackedVersion,
            };

            await _db.InsertAsync(localPackingItem);

            return localPackingItem.LocalPackingItemId;
        }

        public async Task AddPackingItemsAsync(List<PackingItemDTO> items)
        {
            var localPackingItems = items.Select(x => new SQLitePackingItem()
            {
                LocalPackingItemId = x.LocalPackingItemId,
                LocalUserId = x.LocalUserId.ToString(),
                LocalTripId = x.LocalTripId,
                PackingListId = x.PackingListId,
                Name = x.Name,
                CreatedDate = x.CreatedDate,
                IsPacked = x.IsPacked,
                Quantity = Math.Max(1, x.Quantity),
                NameVersion = x.NameVersion,
                QuantityVersion = x.QuantityVersion,
                IsPackedVersion = x.IsPackedVersion,
            }).ToList();

            await _db.InsertAllAsync(localPackingItems);
        }

        public async Task DeletePackingItemAsync(PackingItemDTO item)
        {
            var localPackingItem = new SQLitePackingItem()
            {
                LocalPackingItemId = item.LocalPackingItemId,
                LocalUserId = item.LocalUserId.ToString(),
                //SupabaseItemId = item.Id,
                //TripId = item.TripId,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                //User_id = item.User_id,
                IsPacked = item.IsPacked,
                Quantity = Math.Max(1, item.Quantity),
                NameVersion = item.NameVersion,
                QuantityVersion = item.QuantityVersion,
                IsPackedVersion = item.IsPackedVersion,
            };

            await _db.DeleteAsync(localPackingItem);
        }

        public Task DeletePackingItemsAsync(List<PackingItemDTO> items) => _db.RunInTransactionAsync(db =>
        {
            foreach (var item in items)
                db.Table<SQLitePackingItem>().Where(x => x.LocalPackingItemId == item.LocalPackingItemId
                    && x.LocalUserId == item.LocalUserId && x.LocalTripId == item.LocalTripId).Delete();
        });

        public async Task UpdatePackingItemAsync(PackingItemDTO item)
        {
            var localPackingItem = new SQLitePackingItem()
            {
                LocalUserId = item.LocalUserId,
                LocalPackingItemId = item.LocalPackingItemId,
                RemotePackingItemId = item.RemotePackingItemId,
                RemoteTripId = item.RemoteTripId,
                PackingListId = item.PackingListId,
                LocalTripId = item.LocalTripId,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                RemoteUserId = item.RemoteUserId,
                IsPacked = item.IsPacked,
                Quantity = Math.Max(1, item.Quantity),
                NameVersion = item.NameVersion,
                QuantityVersion = item.QuantityVersion,
                IsPackedVersion = item.IsPackedVersion,
            };

            await _db.UpdateAsync(localPackingItem);
        }

        public async Task<IReadOnlyList<PackingItemDTO>> GetPackingItemsForTripAsync(int localTripId, int remoteTripId)
        {
            var sqlitePackingItem = await _db.Table<SQLitePackingItem>().Where(x => x.LocalTripId == localTripId && !x.IsStaleCache
                && (x.RemoteTripId == null || x.RemoteTripId == 0 || x.RemoteTripId == remoteTripId)).ToListAsync();

            return sqlitePackingItem.Select(x => new PackingItemDTO
            {
                LocalPackingItemId = x.LocalPackingItemId,
                RemotePackingItemId = x.RemotePackingItemId,
                RemoteTripId = x.RemoteTripId,
                LocalTripId = x.LocalTripId,
                PackingListId = x.PackingListId ?? Guid.Empty,
                Name = x.Name,
                CreatedDate = x.CreatedDate,
                LocalUserId = x.LocalUserId,
                RemoteUserId = x.RemoteUserId,
                IsPacked = x.IsPacked,
                Quantity = Math.Max(1, x.Quantity),
                NameVersion = x.NameVersion,
                QuantityVersion = x.QuantityVersion,
                IsPackedVersion = x.IsPackedVersion,
            }).ToList();
        }

        public Task UpdatePendingPackingItems(int localTripId, int remoteTripId)
        {
            return Task.CompletedTask;
        }

        public Task SubscribeToPackingItemChangesAsync(int tripId, Action<PackingItemDTO> onChange)
        {
            return Task.CompletedTask;
        }

        public Task UnsubscribeFromPackingItemChangesAsync() => Task.CompletedTask;

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
    }
}

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

        public async Task<int> AddPackingItemAsync(PackingItemDTO item)
        {
            var localPackingItem = new SQLitePackingItem()
            {
                LocalPackingItemId = item.LocalPackingItemId,
                LocalUserId = item.LocalUserId.ToString(),
                LocalTripId = item.LocalTripId,
                RemotePackingItemId = item.RemotePackingItemId,
                RemoteTripId = item.RemoteTripId,
                RemoteUserId = item.RemoteUserId,
                //ClientId = item..ToString(),
                //SupabaseItemId = item.Id,
                //TripId = item.TripId,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                ModifiedDate = item.ModifiedDate,
                //User_id = item.User_id,
                IsPacked = item.IsPacked,
                Category = item.Category,
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
                Name = x.Name,
                CreatedDate = x.CreatedDate,
                ModifiedDate = x.ModifiedDate,
                IsPacked = x.IsPacked,
                Category = x.Category,
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
                ModifiedDate = item.ModifiedDate,
                //User_id = item.User_id,
                IsPacked = item.IsPacked,
                Category = item.Category,
            };

            await _db.DeleteAsync(localPackingItem);
        }

        public async Task UpdatePackingItemAsync(PackingItemDTO item)
        {
            var localPackingItem = new SQLitePackingItem()
            {
                LocalUserId = item.LocalUserId,
                LocalPackingItemId = item.LocalPackingItemId,
                RemotePackingItemId = item.RemotePackingItemId,
                RemoteTripId = item.RemoteTripId,
                LocalTripId = item.LocalTripId,
                Name = item.Name,
                CreatedDate = item.CreatedDate,
                ModifiedDate = item.ModifiedDate,
                RemoteUserId = item.RemoteUserId,
                IsPacked = item.IsPacked,
                Category = item.Category,
            };

            await _db.UpdateAsync(localPackingItem);
        }

        public async Task<IReadOnlyList<PackingItemDTO>> GetPackingItemsForTripAsync(int localTripId, int remoteTripId)
        {
            var sqlitePackingItem = await _db.Table<SQLitePackingItem>().Where(x => x.LocalTripId == localTripId).ToListAsync();

            return sqlitePackingItem.Select(x => new PackingItemDTO
            {
                LocalPackingItemId = x.LocalPackingItemId,
                RemotePackingItemId = x.RemotePackingItemId,
                RemoteTripId = x.RemoteTripId,
                LocalTripId = x.LocalTripId,
                Name = x.Name,
                CreatedDate = x.CreatedDate,
                ModifiedDate = x.ModifiedDate,
                LocalUserId = x.LocalUserId,
                IsPacked = x.IsPacked,
                Category = x.Category,
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

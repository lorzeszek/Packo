using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using SQLite;
using System.Text.Json;

namespace Packo.Repositories;

public partial class SyncPackingItemRepository : IPackingListRepository
{
    private static readonly TimeSpan ScreenRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _packingRefreshGate = new(1, 1);
    private (string LocalUser, string? RemoteUser, int LocalTrip, int RemoteTrip)? _lastScreenRefreshTrip;
    private long _lastScreenRefreshAt;
    private IPackingListRemote? ListRemote => _remote as IPackingListRemote;
    private bool IsOnline => _sessionService.IsAuthenticated && Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public async Task<(int Pending, bool Conflict)> GetPackingSyncStatusAsync(TripDTO trip)
    {
        var user = _sessionService.LocalUserId;
        if (trip.LocalUserId != user)
            return (0, false);
        var status = (Pending: 0, Conflict: false);
        await _pendingDb.RunInTransactionAsync(db =>
        {
            var items = db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalTripId == trip.LocalTripId && x.LocalUserId == user);
            var lists = db.Table<SQLitePackingList>()
                .Where(x => x.LocalTripId == trip.LocalTripId && x.LocalUserId == user && x.Operation != PackingListOperation.None);
            status = (items.Where(x => x.Operation != "Conflict").Count() + lists.Count(),
                items.Where(x => x.Operation == "Conflict").Delete() > 0);
        });
        return status;
    }

    public Task<IReadOnlyList<PackingItemDTO>> GetPackingListItemsAsync(TripDTO trip, Guid listId, bool refresh = true) =>
        GetPackingListItemsAsync(trip, [listId], refresh);

    public async Task<IReadOnlyList<PackingItemDTO>> GetPackingListItemsAsync(
        TripDTO trip, IReadOnlyCollection<Guid> listIds, bool refresh = true)
    {
        var selectedIds = listIds.ToHashSet();
        var remoteTripId = await ResolveListTripIdAsync(trip, _sessionService.LocalUserId ?? string.Empty);
        var items = refresh ? await GetPackingItemsForTripAsync(trip.LocalTripId, remoteTripId)
            : await GetLocalItemsForCurrentUserAsync(trip.LocalTripId, remoteTripId);
        return items.Where(item => selectedIds.Contains(item.PackingListId)).ToList();
    }

    public async Task RefreshPackingTripAsync(TripDTO trip, bool force = false)
    {
        if (!IsOnline || ListRemote is not { } remote)
            return;
        var userId = _sessionService.LocalUserId ?? string.Empty;
        var remoteUserId = _sessionService.UserId;
        var remoteTripId = await ResolveListTripIdAsync(trip, userId);
        if (remoteTripId <= 0)
            return;

        var key = (userId, remoteUserId, trip.LocalTripId, remoteTripId);
        await _packingRefreshGate.WaitAsync();
        try
        {
            if (!IsOnline || _sessionService.LocalUserId != userId || _sessionService.UserId != remoteUserId)
                return;
            // Details and the editor share the same refresh when opened back-to-back.
            if (!force && _lastScreenRefreshTrip == key && System.Diagnostics.Stopwatch.GetElapsedTime(_lastScreenRefreshAt) < ScreenRefreshInterval)
                return;

            if (await RefreshPackingTripCoreAsync(trip.LocalTripId, remoteTripId, userId, remoteUserId, remote))
            {
                _lastScreenRefreshTrip = key;
                _lastScreenRefreshAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
        finally { _packingRefreshGate.Release(); }
    }

    public async Task<IReadOnlyList<PackingListDTO>> GetPackingListsAsync(TripDTO trip, bool refresh = true)
    {
        var userId = _sessionService.LocalUserId ?? string.Empty;
        var remoteTripId = await ResolveListTripIdAsync(trip, userId);
        if (refresh && IsOnline && remoteTripId > 0 && ListRemote is { } remote)
            await RefreshPackingTripCoreAsync(trip.LocalTripId, remoteTripId, userId, _sessionService.UserId, remote);

        IReadOnlyList<PackingListDTO> result = [];
        await _pendingDb.RunInTransactionAsync(db =>
        {
            if (_sessionService.LocalUserId != userId)
                return;
            var counts = db.Table<SQLitePackingItem>().Where(x => x.LocalUserId == userId
                    && x.LocalTripId == trip.LocalTripId && !x.IsStaleCache
                    && (x.RemoteTripId == null || x.RemoteTripId == 0 || x.RemoteTripId == remoteTripId))
                .ToList().ToLookup(x => x.PackingListId ?? Guid.Empty);
            result = ScopedLists(db, trip.LocalTripId, remoteTripId, userId)
                .ToList().GroupBy(x => x.Id).Select(g => g.OrderByDescending(x => x.Operation).First())
                .Where(x => x.Operation != PackingListOperation.Delete)
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id)
                .Select(x => new PackingListDTO(x.Id, x.Name, counts[x.Id].Count(i => i.IsPacked), counts[x.Id].Count()))
                .ToList();
        });
        return result;
    }

    private async Task<bool> RefreshPackingTripCoreAsync(
        int localTripId, int remoteTripId, string userId, string? remoteUserId, IPackingListRemote remote)
    {
        try
        {
            await SyncPendingChangesAsync();
            await _syncGate.WaitAsync();
            try
            {
                var lists = await remote.GetPackingListsAsync(remoteTripId);
                if (_sessionService.LocalUserId != userId || _sessionService.UserId != remoteUserId)
                    return false;
                await MergePackingListsAsync(localTripId, remoteTripId, userId, lists);
            }
            finally { _syncGate.Release(); }
            await ReadPackingItemsAsync(localTripId, remoteTripId, syncPending: false);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return false;
        }
    }

    public async Task<PackingListDTO> CreatePackingListAsync(TripDTO trip, string name)
    {
        var list = new PackingListDTO(Guid.NewGuid(), ValidateListName(name));
        var userId = _sessionService.LocalUserId ?? throw new InvalidOperationException("A local user is required.");
        var remoteTripId = await ResolveListTripIdAsync(trip, userId);
        await _syncGate.WaitAsync();
        try
        {
            await _pendingDb.InsertAsync(new SQLitePackingList
            {
                Id = list.Id,
                Name = list.Name,
                LocalTripId = trip.LocalTripId,
                LocalUserId = userId,
                RemoteTripId = remoteTripId,
                Operation = PackingListOperation.Create
            });
        }
        finally { _syncGate.Release(); }
        SchedulePendingSync();
        return list;
    }

    public Task RenamePackingListAsync(TripDTO trip, Guid listId, string name) =>
        ChangePackingListAsync(trip, listId, ValidateListName(name));

    public Task DeletePackingListAsync(TripDTO trip, Guid listId) =>
        ChangePackingListAsync(trip, listId, null);

    private async Task ChangePackingListAsync(TripDTO trip, Guid listId, string? name)
    {
        var userId = _sessionService.LocalUserId ?? throw new InvalidOperationException("A local user is required.");
        var remoteTripId = await ResolveListTripIdAsync(trip, userId);
        await _syncGate.WaitAsync();
        try
        {
            await _pendingDb.RunInTransactionAsync(db =>
            {
                var list = ScopedLists(db, trip.LocalTripId, remoteTripId, userId)
                    .Where(x => x.Id == listId).OrderByDescending(x => x.Operation).FirstOrDefault();
                if (list is null || list.Operation == PackingListOperation.Delete)
                    return;

                list.RemoteTripId = remoteTripId;
                if (name is null)
                {
                    list.Operation = PackingListOperation.Delete;
                    RemoveListItems(db, list);
                }
                else
                {
                    list.Name = name;
                    if (list.Operation != PackingListOperation.Create)
                        list.Operation = PackingListOperation.Rename;
                }
                db.Update(list);
            });
            if (name is null)
                await PublishPackingProgressAsync(trip.LocalTripId, remoteTripId, userId);
        }
        finally { _syncGate.Release(); }
        SchedulePendingSync();
    }

    private async Task<int> ResolveListTripIdAsync(TripDTO trip, string userId) =>
        trip.RemoteTripId > 0 ? trip.RemoteTripId
            : (await _tripRepository.GetLocalTripAsync(trip.LocalTripId, userId))?.RemoteTripId ?? 0;

    private static string ValidateListName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > PackingListDTO.MaxNameLength)
            throw new ArgumentException("Invalid packing list name.", nameof(name));
        return trimmed;
    }

    private static TableQuery<SQLitePackingList> ScopedLists(SQLiteConnection db, int localTripId, int remoteTripId, string userId) =>
        db.Table<SQLitePackingList>().Where(x => x.LocalUserId == userId && x.LocalTripId == localTripId
            && (x.RemoteTripId == remoteTripId || x.RemoteTripId == 0));

    private Task MergePackingListsAsync(int localTripId, int remoteTripId, string userId, IReadOnlyList<PackingListDTO> remote) =>
        _pendingDb.RunInTransactionAsync(db =>
        {
            var cached = ScopedLists(db, localTripId, remoteTripId, userId).ToList();
            var remoteIds = remote.Select(x => x.Id).ToHashSet();
            foreach (var row in cached.Where(x => x.Operation != PackingListOperation.Create
                && x.Operation != PackingListOperation.Delete && !remoteIds.Contains(x.Id)))
            {
                RemoveListItems(db, row);
                db.Delete(row);
            }
            foreach (var list in remote)
            {
                var row = cached.FirstOrDefault(x => x.Id == list.Id);
                if (row is null)
                    db.Insert(new SQLitePackingList { Id = list.Id, Name = list.Name, LocalTripId = localTripId,
                        RemoteTripId = remoteTripId, LocalUserId = userId });
                else if (row.Operation == PackingListOperation.None && (row.Name != list.Name || row.RemoteTripId != remoteTripId))
                {
                    row.Name = list.Name;
                    row.RemoteTripId = remoteTripId;
                    db.Update(row);
                }
            }
        });

    private static void RemoveListItems(SQLiteConnection db, SQLitePackingList list)
    {
        var listId = list.Id;
        db.Table<SQLitePackingItem>().Where(x => x.LocalUserId == list.LocalUserId && x.LocalTripId == list.LocalTripId
            && (x.RemoteTripId == null || x.RemoteTripId == 0 || x.RemoteTripId == list.RemoteTripId)
            && (x.PackingListId == listId || (listId == Guid.Empty && x.PackingListId == null))).Delete();

        var pending = db.Table<SQLitePendingPackingItemChange>()
            .Where(x => x.LocalUserId == list.LocalUserId && x.LocalTripId == list.LocalTripId).ToList();
        foreach (var change in pending)
        {
            var item = JsonSerializer.Deserialize<PackingItemDTO>(change.PackingItemJson);
            var tripId = item?.RemoteTripId is > 0 ? item.RemoteTripId.Value : change.RemoteTripId;
            if (item?.PackingListId == listId && (tripId == 0 || tripId == list.RemoteTripId))
                db.Delete(change);
        }
    }

    private async Task SyncPendingListsAsync()
    {
        if (!IsOnline || ListRemote is not { } remote)
            return;

        var localUserId = _sessionService.LocalUserId ?? string.Empty;
        var remoteUserId = _sessionService.UserId;
        await _syncGate.WaitAsync();
        try
        {
            var rows = await _pendingDb.Table<SQLitePackingList>()
                .Where(x => x.LocalUserId == localUserId && x.Operation != PackingListOperation.None).ToListAsync();
            foreach (var row in rows)
            {
                if (!IsOnline || _sessionService.LocalUserId != localUserId || _sessionService.UserId != remoteUserId)
                    return;
                if (row.RemoteTripId == 0)
                {
                    var trip = await _tripRepository.GetLocalTripAsync(row.LocalTripId, localUserId);
                    if (trip?.RemoteTripId is not > 0)
                        continue;
                    row.RemoteTripId = trip.RemoteTripId;
                }

                try
                {
                    if (row.Operation == PackingListOperation.Delete)
                    {
                        await remote.DeletePackingListAsync(row.RemoteTripId, row.Id);
                        await _pendingDb.DeleteAsync(row);
                    }
                    else
                    {
                        await remote.SavePackingListAsync(row.RemoteTripId, new PackingListDTO(row.Id, row.Name),
                            row.Operation == PackingListOperation.Create);
                        row.Operation = PackingListOperation.None;
                        await _pendingDb.UpdateAsync(row);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
        }
        finally { _syncGate.Release(); }
    }

    private Task<SQLitePackingList?> FindItemListAsync(PackingItemDTO item)
    {
        var remoteTripId = item.RemoteTripId ?? 0;
        return _pendingDb.Table<SQLitePackingList>()
            .Where(x => x.LocalUserId == item.LocalUserId && x.LocalTripId == item.LocalTripId
                && x.Id == item.PackingListId && (x.RemoteTripId == 0 || remoteTripId == 0 || x.RemoteTripId == remoteTripId))
            .OrderByDescending(x => x.Operation).FirstOrDefaultAsync()!;
    }

    private async Task ValidateItemListAsync(PackingItemDTO item)
    {
        if (ListRemote is null)
            return;
        var list = await FindItemListAsync(item);
        if (list is null || list.Operation == PackingListOperation.Delete)
            throw new InvalidOperationException("The packing list no longer exists.");
        // An editor opened offline can outlive the upload of its trip and list.
        if (item.RemoteTripId is not > 0 && list.RemoteTripId > 0)
            item.RemoteTripId = list.RemoteTripId;
    }
}

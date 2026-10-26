using Packo.Models.DTO;
using Packo.Models.SQLite;
using SQLite;
using System.Text.Json;

namespace Packo.Repositories.Models;

// A consistent view of local progress and its outstanding uploads, taken in one SQLite transaction.
public sealed class PackingStatsSnapshot
{
    private readonly string _localUserId;
    private readonly Dictionary<(int Local, int Remote), (int Packed, int Total)> _counts;
    private readonly Dictionary<int, HashSet<int>> _remoteTripIds;
    private readonly HashSet<(int Local, int Remote)> _pendingTrips;

    private PackingStatsSnapshot(string localUserId, List<SQLitePackingItem> items, List<SQLitePendingPackingItemChange> pending)
    {
        _localUserId = localUserId;
        _counts = items.GroupBy(x => (Local: x.LocalTripId, Remote: x.RemoteTripId ?? 0))
            .ToDictionary(g => g.Key, g => (g.Count(x => x.IsPacked), g.Count()));
        _pendingTrips = pending.Select(x => (x.LocalTripId, PendingRemoteTripId(x))).ToHashSet();
        _remoteTripIds = items.Select(x => (Local: x.LocalTripId, Remote: x.RemoteTripId ?? 0))
            .Concat(pending.Select(x => (Local: x.LocalTripId, Remote: x.RemoteTripId)))
            .Where(x => x.Remote > 0).GroupBy(x => x.Local)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Remote).ToHashSet());
    }

    private static int PendingRemoteTripId(SQLitePendingPackingItemChange change)
    {
        if (change.RemoteTripId > 0)
            return change.RemoteTripId;
        // Older queue entries stored trip identity only inside their payload.
        try { return JsonSerializer.Deserialize<PackingItemDTO>(change.PackingItemJson)?.RemoteTripId ?? 0; }
        catch (JsonException) { return 0; }
    }

    private bool HasPending(int localTripId, int remoteTripId) =>
        _pendingTrips.Contains((localTripId, remoteTripId)) || _pendingTrips.Contains((localTripId, 0));

    public static async Task<PackingStatsSnapshot> ReadAsync(SQLiteAsyncConnection db, string localUserId)
    {
        PackingStatsSnapshot? snapshot = null;
        await db.RunInTransactionAsync(connection =>
        {
            snapshot = new PackingStatsSnapshot(localUserId,
                connection.Table<SQLitePackingItem>().Where(x => x.LocalUserId == localUserId && !x.IsStaleCache).ToList(),
                connection.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == localUserId).ToList());
        });
        return snapshot!;
    }

    public string SummaryFor(int localTripId, int? remoteTripId = null)
    {
        var (packed, total) = CountsFor(localTripId, remoteTripId);
        return $"{packed} / {total}";
    }

    private (int Packed, int Total) CountsFor(int localTripId, int? remoteTripId)
    {
        var matching = _counts.Where(x => x.Key.Local == localTripId
            && (!remoteTripId.HasValue || x.Key.Remote == remoteTripId || x.Key.Remote == 0)).Select(x => x.Value).ToList();
        return (matching.Sum(x => x.Packed), matching.Sum(x => x.Total));
    }

    public IReadOnlyList<TripWithStats> ApplyTo(IReadOnlyList<TripWithStats> remote, PackingStatsSnapshot beforeRequest)
    {
        return remote.Select(entry =>
        {
            var trip = entry.Trip;
            var id = trip.LocalTripId;
            var belongsToTrip = trip.LocalUserId == _localUserId
                || (_remoteTripIds.TryGetValue(id, out var remoteIds) && remoteIds.Contains(trip.RemoteTripId))
                || (beforeRequest._remoteTripIds.TryGetValue(id, out var previousIds) && previousIds.Contains(trip.RemoteTripId));
            // The upload can finish while the remote RPC is running. Preserve local progress
            // if changes were pending on either side of the request, even if its response is partial.
            var changedDuringRequest = CountsFor(id, trip.RemoteTripId) != beforeRequest.CountsFor(id, trip.RemoteTripId);
            return belongsToTrip && (HasPending(id, trip.RemoteTripId) || beforeRequest.HasPending(id, trip.RemoteTripId) || changedDuringRequest)
                ? new TripWithStats(trip, SummaryFor(id, trip.RemoteTripId))
                : entry;
        }).ToList();
    }
}

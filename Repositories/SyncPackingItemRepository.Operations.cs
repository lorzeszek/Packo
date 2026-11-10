using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Enums;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;

namespace Packo.Repositories;

public partial class SyncPackingItemRepository
{
    private static SQLitePendingPackingItemChange CreateOperation(string operation, PackingItemDTO item) => new()
    {
        OperationId = Guid.NewGuid().ToString(),
        LocalUserId = item.LocalUserId,
        LocalTripId = item.LocalTripId,
        RemoteTripId = item.RemoteTripId ?? 0,
        LocalPackingItemId = item.LocalPackingItemId,
        Operation = operation,
        PackingItemJson = JsonSerializer.Serialize(item)
    };

    private async Task QueueItemUpdateAsync(PackingItemDTO item)
    {
        await ValidateItemListAsync(item);
        await ResolveRemoteIdentityAsync(item);
        await _pendingDb.RunInTransactionAsync(db =>
        {
            var row = db.Find<SQLitePackingItem>(item.LocalPackingItemId);
            if (row is null || row.IsStaleCache || row.LocalUserId != item.LocalUserId || row.LocalTripId != item.LocalTripId)
                throw new InvalidOperationException("The packing item is no longer available.");

            var changes = new Dictionary<string, object>();
            var before = new Dictionary<string, object>();
            var explicitEdit = item.PreviousQuantity.HasValue || item.PreviousIsPacked.HasValue;
            if (!explicitEdit && row.Name != item.Name)
            {
                before["NameVersion"] = row.NameVersion++;
                before["Name"] = row.Name;
                changes["Name"] = row.Name = item.Name;
            }
            if (item.PreviousQuantity.HasValue || (!explicitEdit && row.Quantity != item.Quantity))
            {
                before["QuantityVersion"] = row.QuantityVersion++;
                before["Quantity"] = item.PreviousQuantity ?? row.Quantity;
                changes["Quantity"] = row.Quantity = Math.Max(1, item.Quantity);
            }
            if (item.PreviousIsPacked.HasValue || (!explicitEdit && row.IsPacked != item.IsPacked))
            {
                before["IsPackedVersion"] = row.IsPackedVersion++;
                before["IsPacked"] = item.PreviousIsPacked ?? row.IsPacked;
                changes["IsPacked"] = row.IsPacked = item.IsPacked;
            }
            if (changes.Count == 0)
                return;

            var operation = CreateOperation("Update", item);
            operation.ChangesJson = JsonSerializer.Serialize(changes);
            operation.BeforeJson = JsonSerializer.Serialize(before);
            db.Update(row);
            db.Insert(operation);
        });
    }

    private async Task ApplyQueuedOperationAsync(IPackingOperationRemote remote, SQLitePendingPackingItemChange change,
        PackingItemDTO item, string? account)
    {
        await ResolveRemoteIdentityAsync(item);
        if (change.Operation != "Add" && item.RemotePackingItemId is not > 0)
            return; // The earlier Add must assign its identity first.

        if (!change.Attempted || string.IsNullOrEmpty(change.OperationId))
        {
            if (string.IsNullOrEmpty(change.OperationId))
            {
                if (change.Operation == "Add" && item.RemotePackingItemId is > 0)
                    change.Operation = "Update";
                change.OperationId = Guid.NewGuid().ToString();
            }
            change.Attempted = true;
            await _pendingDb.UpdateAsync(change);
        }

        var tripId = item.RemoteTripId!.Value;
        var changes = change.ChangesJson is not null
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(change.ChangesJson)
            : new Dictionary<string, object> { ["Name"] = item.Name, ["Quantity"] = item.Quantity, ["IsPacked"] = item.IsPacked };
        var parameters = new
        {
            p_trip_id = tripId,
            p_operation_id = change.OperationId,
            p_operation = change.Operation,
            p_item_id = item.RemotePackingItemId,
            p_list_id = item.PackingListId,
            p_changes = changes?.ToDictionary(x => x.Key, x => ToRpcValue(x.Value)),
            p_before = change.BeforeJson is null ? null
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(change.BeforeJson)?
                    .ToDictionary(x => x.Key, x => ToRpcValue(x.Value)),
            p_created_at = item.CreatedDate.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!_sessionService.IsAuthenticated || _sessionService.UserId != account || _sessionService.LocalUserId != change.LocalUserId)
            return;
        // The durable attempt marker lets an edit or delete proceed while the response is in flight.
        _syncGate.Release();
        PackingOperationResult result;
        try { result = await remote.ApplyOperationAsync(parameters); }
        finally { await _syncGate.WaitAsync(); }
        if (_sessionService.UserId != account || _sessionService.LocalUserId != change.LocalUserId)
            return;
        if (result.Status is not ("ok" or "missing" or "conflict" or "denied"))
            throw new InvalidOperationException("Unknown packing operation status.");

        await _pendingDb.RunInTransactionAsync(db =>
        {
            var storedChange = db.Find<SQLitePendingPackingItemChange>(change.Id);
            if (storedChange is null)
                return;
            var row = db.Find<SQLitePackingItem>(item.LocalPackingItemId);
            if (change.Operation == "Add")
            {
                var dependents = db.Table<SQLitePendingPackingItemChange>()
                    .Where(x => x.LocalUserId == item.LocalUserId && x.LocalTripId == item.LocalTripId
                        && x.LocalPackingItemId == item.LocalPackingItemId && x.Id > change.Id).ToList();
                foreach (var dependent in dependents)
                {
                    var queued = JsonSerializer.Deserialize<PackingItemDTO>(dependent.PackingItemJson);
                    if (result.ItemId is null || queued is null)
                        db.Delete(dependent);
                    else
                    {
                        queued.RemotePackingItemId = result.ItemId;
                        queued.RemoteTripId = tripId;
                        dependent.PackingItemJson = JsonSerializer.Serialize(queued);
                        db.Update(dependent);
                    }
                }
                if (row is not null && result.ItemId is int id)
                {
                    row.RemotePackingItemId = id;
                    row.RemoteTripId = tripId;
                    db.Update(row);
                }
            }
            if (row is not null && result.Status is "missing" or "denied")
                db.Delete(row);
            if (result.Status is "missing" or "conflict")
            {
                storedChange.Operation = "Conflict";
                db.Update(storedChange);
            }
            else
                db.Delete(storedChange);
        });

        if (change.Operation == "Add" && result.ItemId is int addedId)
        {
            item.RemotePackingItemId = addedId;
            PackingItemChanged?.Invoke(new PackingItemChange(PackingItemChangeType.IdentityAssigned, item));
        }
        WeakReferenceMessenger.Default.Send(new PackingSyncMessage(tripId,
            Conflict: result.Status is "missing" or "conflict", AccessRevoked: result.Status == "denied"));
    }

    private static object? ToRpcValue(object value) => value is not JsonElement json ? value : json.ValueKind switch
    {
        JsonValueKind.Number => json.GetInt64(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => json.GetString(),
        _ => throw new InvalidOperationException("Unknown packing field value.")
    };
}

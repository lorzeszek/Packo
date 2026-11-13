// Platform and transport substitutes; schedule, SQLite storage and sync service are production code.
using Packo.Models.DTO;
using Packo.Models.SQLite;

public enum NetworkAccess { None, Internet }
public sealed class Connectivity
{
    public static Connectivity Current { get; } = new();
    public NetworkAccess NetworkAccess { get; set; }
}
public static class MainThread { public static void BeginInvokeOnMainThread(Action action) => action(); }

namespace Supabase.Realtime { public sealed class RealtimeChannel { public void Unsubscribe() { } } }
namespace Packo.Services.Interfaces
{
    public interface ISessionService
    {
        string? LocalUserId { get; }
        string? UserId { get; }
        bool IsAuthenticated { get; }
        TestUser? User { get; }
    }
    public sealed class TestUser { public Dictionary<string, object> UserMetadata { get; } = []; }
    public interface ISupabaseService
    {
        FakeClient Client { get; }
        Task EnsureRealtimeConnectedAsync();
    }
    public sealed class FakeClient
    {
        public Func<string, Dictionary<string, object>, Task<Response>> Respond { get; set; } = (_, _) => Task.FromResult(new Response("{}"));
        public Task<Response> Rpc(string name, Dictionary<string, object> args) => Respond(name, args);
    }
    public sealed record Response(string Content);
}
namespace Packo.Repositories.Interfaces
{
    public interface ITripRepository { Task SyncPendingChangesAsync(); }
}
namespace Packo.Repositories.Local
{
    public static class LocalTripRepository
    {
        public static TripDTO ToTrip(SQLiteTrip row) => new()
        {
            LocalTripId = row.LocalTripId, RemoteTripId = row.RemoteTripId ?? 0, LocalUserId = row.LocalUserId,
            RemoteUserId = row.RemoteUserId ?? "", StartDate = row.StartDate, EndDate = row.EndDate,
            TimeZoneId = row.TimeZoneId, IsInTrash = row.IsInTrash, IsJoinedTrip = row.IsJoinedTrip, Destination = row.Destination
        };
    }
}
namespace Packo.Helpers
{
    public static class RealtimeSubscriptionHelper
    {
        public static Task<List<Supabase.Realtime.RealtimeChannel>> SubscribeTableChanges<T>(
            Packo.Services.Interfaces.FakeClient client, Action<T> onInsert, Action<T> onUpdate, Action<T> onDelete)
            => Task.FromResult(new List<Supabase.Realtime.RealtimeChannel>());
    }
}

// In-memory transport for executing the real Supabase repository's query predicates.
// SQLite and the repository, preload, form and detail logic use their production code.
using System.Linq.Expressions;
using Packo.Models.Supabase;
using Packo.Services.Interfaces;
using Supabase.Postgrest.Models;

namespace Packo.Services.Interfaces
{
    public sealed partial class AuthClient
    {
        public List<TripSupabase> TripRows { get; } = [];
        public string StatsJson { get; set; } = "[]";
        public RepositoryTable<T> From<T>() where T : BaseModel, new() => new(this);
        public Task<RpcResponse> Rpc(string name, object? args)
            => Task.FromResult(new RpcResponse(name == "trip_participants" ? "[]" : StatsJson));
    }

    public record RpcResponse(string Content);
    public record ModelResponse<T>(List<T> Models);
    public sealed class RepositoryTable<T>(AuthClient client) where T : BaseModel, new()
    {
        private readonly List<Func<T, bool>> _predicates = [];
        private int _limit = int.MaxValue;
        private Func<T, object>? _order;
        public RepositoryTable<T> Select(string columns) => this;
        public RepositoryTable<T> Order<TValue>(Expression<Func<T, TValue>> column, Supabase.Postgrest.Constants.Ordering ordering)
        {
            var getValue = column.Compile();
            _order = row => getValue(row)!;
            return this;
        }
        public RepositoryTable<T> Limit(int limit) { _limit = limit; return this; }
        public RepositoryTable<T> Where(Expression<Func<T, bool>> predicate)
        {
            _predicates.Add(predicate.Compile());
            return this;
        }
        public Task<ModelResponse<T>> Get()
        {
            var rows = client.TripRows.OfType<T>().Where(row => _predicates.All(predicate => predicate(row)));
            return Task.FromResult(new ModelResponse<T>((_order is null ? rows : rows.OrderBy(_order)).Take(_limit).ToList()));
        }
        public Task Update(T model)
        {
            if (model is not TripSupabase trip) throw new NotSupportedException();
            var index = client.TripRows.FindIndex(row => row.Id == trip.Id);
            if (index < 0) throw new InvalidOperationException("The test trip must exist before editing.");
            client.TripRows[index] = trip;
            return Task.CompletedTask;
        }
        public Task Insert(T model) => throw new NotSupportedException();
        public Task Delete(T model) => throw new NotSupportedException();
    }
}

namespace Supabase.Realtime
{
    public sealed class RealtimeChannel { public void Unsubscribe() { } }
}

namespace Packo.Helpers
{
    public static class RealtimeSubscriptionHelper
    {
        public static Task<List<Supabase.Realtime.RealtimeChannel>> SubscribeTableChanges<T>(
            AuthClient client, Action<T> onInsert, Action<T> onUpdate, Action<T> onDelete)
            => Task.FromResult(new List<Supabase.Realtime.RealtimeChannel>());
    }
}

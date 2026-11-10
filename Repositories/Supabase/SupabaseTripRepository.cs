using Packo.Helpers;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Models.Supabase;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using Supabase.Realtime;
using System.Text.Json;
using static Supabase.Postgrest.Constants;

namespace Packo.Repositories.Supabase
{
    public class SupabaseTripRepository : ITripRepository
    {
        public readonly ISupabaseService _supabase;
        private readonly List<RealtimeChannel> _channels = new();
        private bool _isSubscribed;
        public event Action<TripDTO, string>? TripChanged;


        public SupabaseTripRepository(ISupabaseService supabase)
        {
            _supabase = supabase;
        }

        public async Task StartRealtimeAsync()
        {
            if (_isSubscribed)
                return;

            _isSubscribed = true;

            await _supabase.EnsureRealtimeConnectedAsync();

            _channels.AddRange(
                await RealtimeSubscriptionHelper.SubscribeTableChanges<TripSupabase>(
                    _supabase.Client,
                    onInsert: trip => TripChanged?.Invoke(Mappers.MapToTripDTO(trip), "INSERT"),
                    onUpdate: trip => TripChanged?.Invoke(Mappers.MapToTripDTO(trip), "UPDATE"),
                    onDelete: trip => TripChanged?.Invoke(Mappers.MapToTripDTO(trip), "DELETE")
                )
            );
        }

        public async Task<bool> IsChannelCreatedAsync()
        {
            return _channels.Any();
        }

        public async Task UnsubscribeFromTripChangesAsync()
        {
            if (_channels == null) return;

            foreach (var channel in _channels)
            {
                channel.Unsubscribe();
            }

            _isSubscribed = false;
            _channels.Clear();
        }

        public async Task<int> AddTripAsync(TripDTO trip)
        {
            try
            {
                TripSupabase mappedTrip = Mappers.MapToTripSupabase(trip);

                // Use upsert to make Add idempotent (safe to repeat during sync)
                await _supabase.Client.From<TripSupabase>().Insert(mappedTrip);

                // Supabase C# client doesn't always populate identity after Insert.
                // Fetch the row back by (ClientId, LocalTripId) to get the generated remote id.
                var response = await _supabase.Client
                    .From<TripSupabase>()
                    .Where(x => x.ClientId == trip.LocalUserId && x.LocalTripId == trip.LocalTripId)
                    .Get();

                return response.Models.FirstOrDefault()?.Id ?? mappedTrip.Id;
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                throw;
            }
        }

        public async Task DeleteTripAsync(TripDTO trip)
        {
            try
            {
                TripSupabase mappedTrip = Mappers.MapToTripSupabase(trip);

                await _supabase.Client.From<TripSupabase>().Delete(mappedTrip);
            }
            catch (Exception)
            {

                throw;
            }
        }

        public async Task<TripDTO?> GetTripAsync(TripDTO trip)
        {
            try
            {
                var trips = _supabase.Client.From<TripSupabase>();
                var response = trip.RemoteTripId > 0
                    ? await trips.Where(x => x.Id == trip.RemoteTripId).Get()
                    : await trips.Where(x => x.ClientId == trip.LocalUserId && x.LocalTripId == trip.LocalTripId).Get();

                var tripModel = response.Models.FirstOrDefault();

                return tripModel != null ? Mappers.MapToTripDTO(tripModel) : null;
            }
            catch (Exception)
            {
                // throw new RepositoryException("Failed to delete trip", ex);
                throw;
            }
        }

        public async Task UpdateTripAsync(TripDTO trip)
        {
            try
            {
                TripSupabase mappedTrip = Mappers.MapToTripSupabase(trip);
                if (trip.RemoteTripId > 0)
                {
                    var current = (await _supabase.Client.From<TripSupabase>()
                        .Where(x => x.Id == trip.RemoteTripId).Get()).Models.SingleOrDefault();
                    if (current is null)
                        return;
                    mappedTrip.ClientId = current.ClientId;
                    mappedTrip.LocalTripId = current.LocalTripId;
                    mappedTrip.User_id = current.User_id;
                }
                await _supabase.Client
                        .From<TripSupabase>()
                        .Update(mappedTrip);
            }
            catch (Exception)
            {

                throw;
            }
        }

        public async Task<List<TripItemsStatsSupabase>> GetTripStatsAsync()
        {
            var response = await _supabase.Client.Rpc("count_items_stats_for_all_trips", null);

            if (response?.Content == null)
                return new List<TripItemsStatsSupabase>();

            return JsonSerializer.Deserialize<List<TripItemsStatsSupabase>>(response.Content)
                   ?? new List<TripItemsStatsSupabase>();
        }

        public async Task<IReadOnlyList<TripSupabase>> GetActiveTripsAsync()
        {
            var response = await _supabase.Client
                .From<TripSupabase>()
                .Select("*")
                .Where(x => x.IsActive == true && x.IsInTrash == false)
                .Get();

            return response.Models ?? new List<TripSupabase>();
        }

        public async Task<IReadOnlyList<TripWithStats>> GetActiveTripsWithStatsAsync()
        {
            // The Trips list and details include completed trips as well as active ones.
            // Only trashed trips are hidden; IsActive describes the schedule, not visibility.
            const int pageSize = 250;
            var trips = new List<TripSupabase>();
            var lastId = 0;
            while (true)
            {
                var response = await _supabase.Client.From<TripSupabase>()
                    .Where(x => x.IsInTrash == false).Where(x => x.Id > lastId)
                    .Order(x => x.Id, Ordering.Ascending).Limit(pageSize).Get();
                trips.AddRange(response.Models);
                if (response.Models.Count < pageSize)
                    break;
                lastId = response.Models[^1].Id;
            }

            // 2. Wywołanie RPC do pobrania statystyk
            var statsResponse = await _supabase.Client.Rpc("packing_trip_stats", new { });

            var stats = statsResponse?.Content == null
                ? []
                : JsonSerializer.Deserialize<List<PackingTripStats>>(statsResponse.Content) ?? [];
            var participantResponse = await _supabase.Client.Rpc("trip_participants", new { });
            var participants = (JsonSerializer.Deserialize<List<TripParticipant>>(participantResponse.Content ?? "[]") ?? [])
                .ToLookup(x => x.TripId);

            // 3. Połączenie tripów ze statystykami
            var result = trips.Select(trip =>
            {
                var stat = stats.FirstOrDefault(s => s.TripId == trip.Id);
                var summary = stat == null
                    ? "0 / 0"
                    : $"{stat.Packed} / {stat.Total}";
                var tripDTO = Mappers.MapToTripDTO(trip);
                tripDTO.Participants = participants[trip.Id].ToList();
                return new TripWithStats(tripDTO, summary);
            }).ToList();

            return result;
        }

        private sealed record PackingTripStats(int TripId, int Packed, int Total);

        public async Task<IReadOnlyList<string>> GetActiveDestinationsAsync()
        {
            // Include upcoming trips even if their cached IsActive flag is false, and
            // exclude trips that have already ended or were moved to trash.
            var response = await _supabase.Client
                .From<TripSupabase>()
                .Select("*")
                .Where(x => x.IsInTrash == false)
                .Get();

            var now = DateTime.Now;
            var activeTrips = (response.Models ?? new List<TripSupabase>())
                .Where(trip => trip.EndDate.HasValue && trip.EndDate.Value > now)
                .Where(trip => !string.IsNullOrWhiteSpace(trip.Destination))
                .OrderByDescending(trip => trip.StartDate.HasValue && trip.StartDate.Value <= now)
                .ThenBy(trip => trip.StartDate ?? DateTime.MaxValue);

            return activeTrips
                .Select(trip => trip.Destination)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Task SyncPendingChangesAsync()
        {
            return Task.CompletedTask;
        }

        public Task<TripDTO?> GetLocalTripAsync(int localTripId, string localUserId)
        {
            return Task.FromResult<TripDTO?>(new TripDTO());
        }

        public Task DeleteLocalTrips()
        {
            return Task.CompletedTask;
        }

        public Task SaveDocAsync(TripDocumentDTO document)
        {
            return Task.CompletedTask;
        }

        public Task<List<SQLiteTripDocument>> GetByTripDocsAsync()
        {
            return Task.FromResult(new List<SQLiteTripDocument>());
        }

        public async Task DeleteDocAsync(Guid id)
        {
            await Task.CompletedTask;
        }
    }
}

using Packo.Helpers;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Models.Supabase;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using Supabase.Realtime;
using System.Text.Json;

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
                var response = await _supabase.Client.From<TripSupabase>().Where(x => x.ClientId == trip.LocalUserId && x.LocalTripId == trip.LocalTripId).Get();

                var tripModel = response.Models.FirstOrDefault();

                return tripModel != null ? Mappers.MapToTripDTO(tripModel) : new TripDTO();
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
            // 1. Pobranie aktywnych tripów
            var tripsResponse = await _supabase.Client
                    .From<TripSupabase>()
                    .Select("*")
                    .Where(x => x.IsActive == true && x.IsInTrash == false)
                    .Get();

            var trips = tripsResponse.Models ?? new List<TripSupabase>();

            // 2. Wywołanie RPC do pobrania statystyk
            var statsResponse = await _supabase.Client.Rpc("count_items_stats_for_all_trips", new { });

            var stats = statsResponse?.Content == null
                ? new List<TripItemsStatsSupabase>()
                : JsonSerializer.Deserialize<List<TripItemsStatsSupabase>>(statsResponse.Content)
                  ?? new List<TripItemsStatsSupabase>();

            // 3. Połączenie tripów ze statystykami
            var result = trips.Select(trip =>
            {
                var stat = stats.FirstOrDefault(s => s.TripId == trip.Id);
                var summary = stat == null
                    ? "0 / 0"
                    : $"{stat.IsPackedCount} / {stat.IsPackedCount + stat.IsNotPackedCount}";
                var tripDTO = Mappers.MapToTripDTO(trip);
                return new TripWithStats(tripDTO, summary);
            }).ToList();

            return result;
        }

        public async Task<IReadOnlyList<string>> GetActiveDestinationsAsync()
        {
            // Include upcoming trips even if their cached IsActive flag is false, and
            // exclude trips that have already ended or were moved to trash.
            var response = await _supabase.Client
                .From<TripSupabase>()
                .Select("*")
                .Where(x => x.IsInTrash == false)
                .Get();

            var today = DateTime.Today;
            var activeTrips = (response.Models ?? new List<TripSupabase>())
                .Where(trip => trip.EndDate.HasValue && trip.EndDate.Value.Date >= today)
                .Where(trip => !string.IsNullOrWhiteSpace(trip.Destination))
                .OrderByDescending(trip => trip.StartDate.HasValue && trip.StartDate.Value.Date <= today)
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

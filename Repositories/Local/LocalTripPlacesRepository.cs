using System.Text.Json;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Interfaces;
using SQLite;

namespace Packo.Repositories.Local;

// A personal list on this device, scoped to the local account and trip.
public sealed class LocalTripPlacesRepository(SQLiteAsyncConnection db) : ITripPlacesRepository
{
    public async Task<TripPlacesState> LoadAsync(TripDTO trip)
    {
        Validate(trip);
        TripPlacesState state = TripPlacesState.Empty;
        await db.RunInTransactionAsync(connection => state = Read(connection, trip));
        return state;
    }

    public Task<TripPlacesState> ReplaceSuggestionsAsync(TripDTO trip, string contextKey, IReadOnlyList<TripPlace> places) =>
        ChangeAsync(trip, state => state with
        {
            ContextKey = contextKey,
            Suggestions = places.DistinctBy(x => x.Id).Take(8).ToList(),
            SeenIds = (state.ContextKey == contextKey ? state.SeenIds ?? [] : [])
                .Concat(places.Select(x => x.Id)).Distinct().TakeLast(200).ToList()
        });

    public Task<TripPlacesState> SaveAsync(TripDTO trip, TripPlace place) =>
        ChangeAsync(trip, state => state.Saved.Any(x => x.Place.Id == place.Id) ? state
            : state with { Saved = [.. state.Saved, new SavedTripPlace(place)] });

    public Task<TripPlacesState> RemoveAsync(TripDTO trip, string placeId) =>
        ChangeAsync(trip, state => state with { Saved = state.Saved.Where(x => x.Place.Id != placeId).ToList() });

    public Task<TripPlacesState> SetVisitedAsync(TripDTO trip, string placeId, bool visited) =>
        ChangeAsync(trip, state => state with
        {
            Saved = state.Saved.Select(x => x.Place.Id == placeId ? x with { Visited = visited } : x).ToList()
        });

    private async Task<TripPlacesState> ChangeAsync(TripDTO trip, Func<TripPlacesState, TripPlacesState> change)
    {
        Validate(trip);
        TripPlacesState updated = TripPlacesState.Empty;
        await db.RunInTransactionAsync(connection =>
        {
            // Do not recreate orphan data after a trip was deleted while a request was running.
            if (connection.Find<SQLiteTrip>(trip.LocalTripId) is not { } row || row.LocalUserId != trip.LocalUserId || row.IsInTrash)
                throw new InvalidOperationException("The trip is no longer available.");
            updated = change(Read(connection, trip));
            connection.InsertOrReplace(new SQLiteTripPlaces
            {
                Key = Key(trip), LocalUserId = trip.LocalUserId, LocalTripId = trip.LocalTripId,
                RemoteTripId = trip.RemoteTripId, StateJson = JsonSerializer.Serialize(updated)
            });
        });
        return updated;
    }

    private static TripPlacesState Read(SQLiteConnection connection, TripDTO trip)
    {
        var row = connection.Find<SQLiteTripPlaces>(Key(trip));
        return row is null ? TripPlacesState.Empty
            : JsonSerializer.Deserialize<TripPlacesState>(row.StateJson) ?? TripPlacesState.Empty;
    }

    private static string Key(TripDTO trip) => $"{trip.LocalUserId}:{trip.LocalTripId}";
    private static void Validate(TripDTO trip)
    {
        if (trip.LocalTripId <= 0 || string.IsNullOrWhiteSpace(trip.LocalUserId))
            throw new ArgumentException("A local account and trip are required.");
    }
}

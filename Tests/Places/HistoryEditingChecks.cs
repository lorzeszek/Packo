using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;
using SQLite;
using System.Text.Json.Nodes;

internal static class HistoryEditingChecks
{
    public static async Task RunAsync(SQLiteAsyncConnection db, ITripPlacesRepository repository, Action<bool, string> check)
    {
        var row = new SQLiteTrip { LocalUserId = "alice", Destination = "Ostróda" };
        await db.InsertAsync(row);
        var trip = new TripDTO { LocalTripId = row.LocalTripId, LocalUserId = "alice", Destination = row.Destination };
        var fountain = new TripPlace { Id = "google:TestFountain", Name = "Test fountain" };
        var lake = new TripPlace { Id = "google:TestLake", Name = "Test lake" };
        var park = new TripPlace { Id = "google:TestPark", Name = "Test park" };
        var finder = new FilteringFinder([fountain, lake, park]);
        var session = new Session();
        await repository.ReplaceSuggestionsAsync(trip, PlaceSuggestionService.ContextKey(trip, "pl"), [fountain, lake]);
        await repository.SaveAsync(trip, fountain with { UserLabel = "Evening walk", UserNote = "After dinner" });
        await repository.SetVisitedAsync(trip, fountain.Id, true);
        // A real SQLite record written by older versions may still contain SeenIds.
        var key = $"{trip.LocalUserId}:{trip.LocalTripId}";
        var stored = (await db.FindAsync<SQLiteTripPlaces>(key))!;
        var legacy = JsonNode.Parse(stored.StateJson)!;
        legacy["SeenIds"] = new JsonArray(JsonValue.Create(fountain.Id), JsonValue.Create(lake.Id), JsonValue.Create(park.Id));
        stored.StateJson = legacy.ToJsonString();
        await db.UpdateAsync(stored);
        var view = new TripPlacesViewModel(repository, finder, session);
        await view.LoadAsync(trip);
        view.ShowSaved = false;
        view.SetOnline(true);
        await view.FindCommand.ExecuteAsync(null);
        check(finder.Excluded.SequenceEqual([fountain.Id])
            && view.Items.Select(x => x.Place.Id).ToHashSet().SetEquals([lake.Id, park.Id]),
            "The single search action ignores legacy seen-place history and excludes only saved places");
        var migrated = await repository.LoadAsync(trip);
        check(migrated.Saved.Single().Visited && migrated.Saved.Single().Place.UserLabel == "Evening walk"
            && migrated.Saved.Single().Place.UserNote == "After dinner"
            && JsonNode.Parse((await db.FindAsync<SQLiteTripPlaces>(key))!.StateJson)!["SeenIds"] is null,
            "Old stored history is retired without losing saved places, labels, notes or visited status");
        var requestsBeforeRepeat = finder.Requests;
        await view.FindCommand.ExecuteAsync(null);
        check(finder.Requests == requestsBeforeRepeat + 1 && finder.Excluded.SequenceEqual([fountain.Id])
            && view.Items.Select(x => x.Place.Id).ToHashSet().SetEquals([lake.Id, park.Id]),
            "Repeated searches make one request each and can return the same unsaved places");
        await view.SaveCommand.ExecuteAsync(view.Items.Single(x => x.Place.Id == lake.Id));
        await view.FindCommand.ExecuteAsync(null);
        check(finder.Excluded.ToHashSet().SetEquals([fountain.Id, lake.Id]) && view.Items.Single().Place.Id == park.Id,
            "Saving a suggestion excludes it on the next search");
        view.ShowSaved = true;
        view.SetOnline(false);
        await view.RemoveCommand.ExecuteAsync(view.Items.Single(x => x.Place.Id == fountain.Id));
        view = new TripPlacesViewModel(repository, finder, session);
        await view.LoadAsync(trip);
        view.ShowSaved = false;
        view.SetOnline(true);
        await view.FindCommand.ExecuteAsync(null);
        check(finder.Excluded.SequenceEqual([lake.Id]) && view.Items.Any(x => x.Place.Id == fountain.Id),
            "A removed place can be suggested again after reloading without a separate reset action");
        view.ShowSaved = true;
        await view.EditSavedPlaceAsync(lake with { UserLabel = "Lake walk", UserNote = "After dinner" });
        await view.VisitCommand.ExecuteAsync(view.Items.Single());
        view.SetOnline(false);

        var manual = new TripPlace { Id = "manual:edit", Name = "Old name", LocationText = "Old address", Category = "manual" };
        await view.AddManualAsync(manual);
        await view.VisitCommand.ExecuteAsync(view.Items.Single(x => x.Place.Id == manual.Id));
        await view.EditSavedPlaceAsync(manual with { Name = "New name", LocationText = "New address",
            Description = "Meet at noon", WebsiteUrl = "https://museum.example/" });
        var edited = (await repository.LoadAsync(trip)).Saved.Single(x => x.Place.Id == manual.Id);
        check(edited.Visited && edited.Place.Name == "New name" && edited.Place.LocationText == "New address"
            && edited.Place.Description == "Meet at noon" && edited.Place.WebsiteUrl == "https://museum.example/",
            "Editing a manual place offline updates its name, address, note and website while retaining its identity and visited status");
        view = new TripPlacesViewModel(repository, finder, session);
        await view.LoadAsync(trip);
        var manualCard = view.Items.Single(x => x.Place.Id == manual.Id);
        check(view.SavedCount == 2 && manualCard.IsManual && manualCard.IsVisited && manualCard.DisplayName == "New name"
            && manualCard.DisplayDescription == "Meet at noon" && manualCard.HasWebsite,
            "Manual edits appear after reloading without duplicate saved entries");
        await view.EditSavedPlaceAsync(manual with { Id = "manual:missing", Name = "Do not recreate" });
        check((await repository.LoadAsync(trip)).Saved.Count == 2,
            "An old editor result cannot recreate a saved entry that no longer exists");

        await repository.SaveAsync(trip, lake);
        await view.LoadAsync(trip);
        await view.ClearSavedAsync();
        var cleared = await repository.LoadAsync(trip);
        check(cleared.Saved.Count == 0, "Clearing saved places removes the current exclusion list");
        await view.LoadAsync(trip);
        view.SetOnline(true);
        await view.FindCommand.ExecuteAsync(null);
        check(finder.Excluded.Count == 0 && view.Items.Count == 3,
            "One regular search after clearing can return all unsaved places again");
        foreach (var card in view.Items.ToArray()) await view.SaveCommand.ExecuteAsync(card);
        await view.FindCommand.ExecuteAsync(null);
        check(view.Items.Count == 0 && (await repository.LoadAsync(trip)).Suggestions.Count == 0
            && view.SavedCount == 3 && view.HasMessage,
            "An empty fresh search clears stale suggestion cards while preserving the saved list");
        finder.ReturnUnfiltered = true;
        await view.FindCommand.ExecuteAsync(null);
        check(view.Items.Count == 0 && view.SavedCount == 3,
            "Saved places are also filtered locally when the provider returns an excluded place");
    }

    private sealed class FilteringFinder(IReadOnlyList<TripPlace> places) : IPlaceSuggestionService
    {
        public int Requests;
        public bool ReturnUnfiltered;
        public IReadOnlyCollection<string> Excluded = [];
        public Task<IReadOnlyList<TripPlace>> FindAsync(TripDTO trip, string language, IReadOnlyCollection<string> excludedIds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests++;
            Excluded = excludedIds.ToArray();
            return Task.FromResult<IReadOnlyList<TripPlace>>(places.Where(x => ReturnUnfiltered || !excludedIds.Contains(x.Id)).ToList());
        }
    }
}

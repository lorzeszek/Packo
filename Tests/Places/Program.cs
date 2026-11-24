using System.ComponentModel;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Local;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;
using SQLite;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    checks++;
}
async Task MustFail(Func<Task> action)
{
    try { await action(); } catch (InvalidOperationException) { return; }
    throw new Exception("Expected rejection of unavailable trip");
}

var path = Path.Combine(Path.GetTempPath(), "travelset-places-" + Guid.NewGuid() + ".db3");
var db = new SQLiteAsyncConnection(path);
try
{
    await db.CreateTableAsync<SQLiteTrip>();
    await db.CreateTableAsync<SQLiteTripPlaces>();
    var tripRow = new SQLiteTrip { LocalUserId = "alice", Destination = "Warszawa" };
    await db.InsertAsync(tripRow);
    var otherRow = new SQLiteTrip { LocalUserId = "alice", Destination = "Kraków" };
    await db.InsertAsync(otherRow);
    var trip = new TripDTO { LocalTripId = tripRow.LocalTripId, LocalUserId = "alice", Destination = "Warszawa" };
    var otherTrip = new TripDTO { LocalTripId = otherRow.LocalTripId, LocalUserId = "alice", Destination = "Kraków" };
    var repository = new LocalTripPlacesRepository(db);
    var session = new Session();
    var finder = new Finder();
    var vm = new TripPlacesViewModel(repository, finder, session);
    vm.Activate();
    await vm.LoadAsync(trip);
    Check(finder.Requests == 0 && vm.Items.Count == 0, "Opening a trip never generates places automatically");
    vm.SetOnline(false);
    await vm.FindCommand.ExecuteAsync(null);
    Check(!vm.FindCommand.CanExecute(null) && finder.Requests == 0 && vm.IsOffline,
        "Offline search is disabled and cannot send a request even if executed directly");
    vm.SetOnline(true);
    await vm.FindCommand.ExecuteAsync(null);
    Check(finder.Requests == 1 && vm.Items.Count == 1, "An explicit online request loads suggestions");
    await vm.SaveCommand.ExecuteAsync(vm.Items[0]);
    await vm.SaveCommand.ExecuteAsync(vm.Items[0]);
    Check((await repository.LoadAsync(trip)).Saved.Count == 1, "Repeated saves cannot duplicate a place");
    Check((await repository.LoadAsync(otherTrip)).Saved.Count == 0,
        "A trip's saved places never appear in another trip");
    var wrongAccount = trip.CopyForEditing();
    wrongAccount.LocalUserId = "bob";
    Check((await repository.LoadAsync(wrongAccount)).Saved.Count == 0, "Accounts have separate saved-place lists");
    vm.SetOnline(false);
    vm.ShowSaved = true;
    await vm.VisitCommand.ExecuteAsync(vm.Items[0]);
    Check(vm.Items.Single().IsVisited, "Visited status can be edited without a network connection");
    await vm.AddManualAsync(new TripPlace { Id = "manual:1", Name = "Meeting point", LocationText = "Warszawa" });
    Check(vm.Items.Count == 2, "A user can add their own place offline");
    await db.CloseAsync();
    db = new SQLiteAsyncConnection(path);
    repository = new LocalTripPlacesRepository(db);
    var restarted = new TripPlacesViewModel(repository, finder, session);
    restarted.SetOnline(false);
    await restarted.LoadAsync(trip);
    Check(restarted.ShowSaved && restarted.Items.Count == 2 && restarted.Items[0].IsVisited && finder.Requests == 1,
        "Saved places, descriptions, links and visited status survive a restart offline");
    await restarted.RemoveCommand.ExecuteAsync(restarted.Items[1]);
    Check((await repository.LoadAsync(trip)).Saved.Count == 1, "Removing a saved place works offline");
    trip.Destination = "Gdańsk";
    await restarted.LoadAsync(trip);
    restarted.ShowSaved = false;
    Check(restarted.Items.Count == 0 && (await repository.LoadAsync(trip)).Saved.Count == 1,
        "Changing destination invalidates suggestions without deleting saved places");
    var suspended = new Finder { Block = true };
    var cancelled = new TripPlacesViewModel(repository, suspended, session);
    await cancelled.LoadAsync(trip);
    cancelled.SetOnline(true);
    var running = cancelled.FindCommand.ExecuteAsync(null);
    await suspended.Started.Task;
    cancelled.SetOnline(false);
    await running;
    Check(!cancelled.IsWorking && (await repository.LoadAsync(trip)).ContextKey != PlaceSuggestionService.ContextKey(trip, "pl"),
        "Losing the connection cancels generation without replacing the saved state");
    cancelled.Activate();
    cancelled.SetOnline(true);
    running = cancelled.FindCommand.ExecuteAsync(null);
    session.ChangeAccount("bob");
    await running;
    Check(cancelled.Items.Count == 0 && !cancelled.CanEdit, "Switching accounts cancels requests and clears visible personal data");
    cancelled.Deactivate();
    session.ChangeAccount("alice");
    vm.Deactivate();
    var pausedRepository = new PausedRepository(repository);
    var paused = new TripPlacesViewModel(pausedRepository, finder, session);
    paused.Activate();
    await paused.LoadAsync(trip);
    paused.SetOnline(true);
    running = paused.FindCommand.ExecuteAsync(null);
    await pausedRepository.Written.Task;
    session.ChangeAccount("bob");
    pausedRepository.Resume.TrySetResult();
    await running;
    Check(paused.Items.Count == 0 && !paused.CanEdit,
        "An account change during the final database write cannot restore the previous account's visible data");
    paused.Deactivate();
    session.ChangeAccount("alice");
    tripRow.IsInTrash = true;
    await db.UpdateAsync(tripRow);
    await MustFail(() => repository.SaveAsync(trip, new TripPlace { Id = "orphan", Name = "Orphan" }));
    await MustFail(() => repository.ClearSavedAsync(trip));
    Check(true, "A deleted or trashed trip cannot acquire new saved data from a late request");

    Check(TripPlace.WebUri("javascript:alert(1)") is null && TripPlace.WebUri("file:///secret") is null
        && TripPlace.WebUri("https://user:password@example.com") is null && TripPlace.WebUri("https://museum.example") is not null,
        "Only ordinary HTTP(S) website links can be opened");
    var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");
    var map = new TripPlace { Name = "A & B", Latitude = 52.25, Longitude = 21.01 }.MapUri;
    System.Globalization.CultureInfo.CurrentCulture = previousCulture;
    Check(Uri.UnescapeDataString(map.Query).Contains("52.25,21.01"), "Map coordinates are independent of the Polish decimal separator");

    await GatewayChecks.RunAsync(repository, otherTrip, Check);

    var bobRow = new SQLiteTrip { LocalUserId = "bob", Destination = "Toruń" };
    await db.InsertAsync(bobRow);
    var bobTrip = new TripDTO { LocalTripId = bobRow.LocalTripId, LocalUserId = "bob", Destination = "Toruń" };
    await repository.SaveAsync(bobTrip, new TripPlace { Id = "manual:bob", Name = "Bob's place" });
    await repository.SaveAsync(otherTrip, new TripPlace { Id = "manual:clear", Name = "Clear me", UserNote = "A local note" });
    var beforeClear = await repository.LoadAsync(otherTrip);
    var clearView = new TripPlacesViewModel(repository, finder, session);
    await clearView.LoadAsync(otherTrip);
    clearView.SetOnline(false);
    Check(clearView.CanClearSaved && clearView.SavedCount == 2,
        "A nonempty saved list can be cleared offline");
    var requestsBeforeClear = finder.Requests;
    await clearView.ClearSavedAsync();
    var afterClear = await repository.LoadAsync(otherTrip);
    Check(clearView.Items.Count == 0 && !clearView.HasSaved && !clearView.CanClearSaved
        && afterClear.Saved.Count == 0 && finder.Requests == requestsBeforeClear,
        "Clearing saved places removes all entries and notes in one local operation without API requests");
    Check(afterClear.ContextKey == beforeClear.ContextKey
        && afterClear.Suggestions.Select(x => x.Id).SequenceEqual(beforeClear.Suggestions.Select(x => x.Id))
        && (await repository.LoadAsync(trip)).Saved.Count == 1
        && (await repository.LoadAsync(bobTrip)).Saved.Count == 1,
        "Clearing one list preserves suggestions, other trips and other accounts");
    var clearedRestart = new TripPlacesViewModel(new LocalTripPlacesRepository(db), finder, session);
    await clearedRestart.LoadAsync(otherTrip);
    await clearedRestart.ClearSavedAsync();
    Check(clearedRestart.SavedCount == 0 && !clearedRestart.CanClearSaved,
        "An empty saved list remains empty on reload and cannot be cleared again");

    await HistoryEditingChecks.RunAsync(db, repository, Check);

    await db.CreateTableAsync<SQLiteUser>();
    await db.InsertAsync(new SQLiteUser { LocalUserId = "guest" });
    await db.InsertAsync(new SQLiteUser { LocalUserId = "account", SupabaseUserId = "cloud-account" });
    foreach (var table in new[] { "SQLitePackingItem", "SQLitePackingList", "SQLitePendingPackingItemChange", "SQLitePendingTripChange" })
        await db.ExecuteAsync($"CREATE TABLE {table} (LocalUserId TEXT, LocalTripId INTEGER)");
    var draftRow = new SQLiteTrip { LocalUserId = "guest", Destination = "Poznań" };
    await db.InsertAsync(draftRow);
    var draft = new TripDTO { LocalTripId = draftRow.LocalTripId, LocalUserId = "guest", Destination = "Poznań" };
    await repository.SaveAsync(draft, new TripPlace { Id = "manual:draft", Name = "Draft place" });
    var users = new LocalUserService(db);
    await users.GetOrCreateAccountAsync("cloud-account", attachGuestDrafts: true);
    draft.LocalUserId = "account";
    Check((await repository.LoadAsync(draft)).Saved.Single().Place.Name == "Draft place",
        "Attaching a guest trip to an existing account transfers the saved-place list and its identity");

    Console.WriteLine($"{checks} places checks passed.");
}
finally
{
    await db.CloseAsync();
    SQLiteAsyncConnection.ResetPool();
    File.Delete(path);
}

sealed class Session : ISessionService, INotifyPropertyChanged
{
    public string? LocalUserId { get; private set; } = "alice";
    public bool GlobalIsBusy => false;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void ChangeAccount(string id) { LocalUserId = id; PropertyChanged?.Invoke(this, new(nameof(LocalUserId))); }
}
sealed class PausedRepository(ITripPlacesRepository inner) : ITripPlacesRepository
{
    public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<TripPlacesState> LoadAsync(TripDTO trip) => inner.LoadAsync(trip);
    public async Task<TripPlacesState> ReplaceSuggestionsAsync(TripDTO trip, string context, IReadOnlyList<TripPlace> places)
    {
        var state = await inner.ReplaceSuggestionsAsync(trip, context, places);
        Written.TrySetResult();
        await Resume.Task;
        return state;
    }
    public Task<TripPlacesState> SaveAsync(TripDTO trip, TripPlace place) => inner.SaveAsync(trip, place);
    public Task<TripPlacesState> RemoveAsync(TripDTO trip, string id) => inner.RemoveAsync(trip, id);
    public Task<TripPlacesState> ClearSavedAsync(TripDTO trip) => inner.ClearSavedAsync(trip);
    public Task<TripPlacesState> SetVisitedAsync(TripDTO trip, string id, bool visited) => inner.SetVisitedAsync(trip, id, visited);
}
sealed class Finder : IPlaceSuggestionService
{
    public int Requests;
    public bool Block;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<IReadOnlyList<TripPlace>> FindAsync(TripDTO trip, string language, IReadOnlyCollection<string> excludedIds, CancellationToken token)
    {
        Requests++;
        Started.TrySetResult();
        if (Block) await Task.Delay(Timeout.Infinite, token);
        return [new TripPlace { Id = "Q1", Name = "Museum", Description = "A museum", WebsiteUrl = "https://museum.example" }];
    }
}

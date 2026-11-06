using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Models.DTO;
using Packo.Repositories.Models;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;

var nearest = Trip(1, 10, "Nearest", DateTime.Today);
var later = Trip(2, 20, "Selected", DateTime.Today.AddDays(60));
var past = Trip(3, 30, "Past", DateTime.Today.AddDays(-60));
var preload = new Preload { Trips = [new(nearest, "0 / 4"), new(later, "2 / 8"), new(past, "1 / 3")] };
var vm = new Details(preload);
await vm.SelectAsync(later);
await vm.OnAppearingAsync();
Check(vm.TripDestination == "Selected" && vm.PackingProgress == .25,
    "Details show the selected trip and its progress instead of the nearest trip");
Check(vm.Title == "TripDetails", "Details use a stable title");

await vm.OpenPackingListCommand.ExecuteAsync(null);
Check(Shell.Current.Route == "PackingListPage"
    && ReferenceEquals(Shell.Current.Query?["trip"], later),
    "Packing from details navigates with the selected trip's identities and schedule");
await vm.EditTripCommand.ExecuteAsync(null);
Check(Shell.Current.Route == "TripSetupPage" && ReferenceEquals(Shell.Current.Query!["editTrip"], later),
    "Editing opens the selected trip");

var updated = Trip(2, 20, "Updated destination", later.StartDate!.Value);
preload.Trips = [new(nearest, "0 / 4"), new(updated, "6 / 8")];
await vm.OnAppearingAsync();
Check(vm.TripDestination == "Updated destination" && vm.PackingProgress == .75,
    "Returning from editing or packing reloads the same trip's details and progress");

WeakReferenceMessenger.Default.Send(new PackingProgressChangedMessage(1, 10, "local", "4 / 4"));
Check(vm.PackingSummary == "6 / 8", "Packing updates for another trip cannot change the selected trip");
WeakReferenceMessenger.Default.Send(new PackingProgressChangedMessage(2, 20, "local", "7 / 8"));
Check(vm.PackingSummary == "7 / 8" && vm.PackingProgress == .875,
    "Packing updates immediately refresh the selected trip's progress");

preload.Trips = [new(nearest, "0 / 4"), new(past, "1 / 3")];
await vm.SelectAsync(past);
await vm.OnAppearingAsync();
Check(vm.HasTrip && vm.TripDestination == "Past" && vm.TripCountdown == "TripEnded",
    "Ended trips remain accessible through details");

var offline = Trip(7, 0, "Offline trip", DateTime.Today.AddDays(5));
var otherUser = Trip(7, 0, "Different owner's trip", DateTime.Today);
otherUser.LocalUserId = "other";
preload.Trips = [new(otherUser, "9 / 9"), new(offline, "1 / 5")];
await vm.SelectAsync(offline);
await vm.OnAppearingAsync();
Check(vm.TripDestination == "Offline trip" && vm.PackingSummary == "1 / 5",
    "Unsynced trips use both the local ID and owner when selecting details");

preload.Trips = [new(nearest, "0 / 4")];
await vm.OnAppearingAsync();
Check(vm.Trip == null && vm.ShowTripUnavailable && !vm.HasLoadError,
    "A missing trip shows an unavailable state instead of switching to another trip");
preload.Fail = true;
await vm.OnAppearingAsync();
Check(vm.HasLoadError && vm.IsDataLoaded && !vm.ShowTripUnavailable,
    "Loading failures offer retry without reporting the trip as deleted");
preload.Fail = false;
preload.Trips = [new(offline, "3 / 5")];
await vm.RetryLoadCommand.ExecuteAsync(null);
Check(preload.ForceRefresh && vm.TripDestination == "Offline trip" && !vm.HasLoadError,
    "Retry refreshes data and recovers the original selected trip");

LocalizationResourceManager.Instance.ChangeLanguage();
Check(vm.Title == "TripDetails", "Language changes keep the trip details heading");
var withoutSelection = new Details(preload);
await withoutSelection.OnAppearingAsync();
Check(withoutSelection.ShowTripUnavailable && withoutSelection.Trip == null,
    "Details without a selection never choose an unrelated trip");
await vm.BackToTripsCommand.ExecuteAsync(null);
Check(Shell.Current.Route == "//TripList", "The unavailable view returns to Trips");
await TripDeletionChecks.RunAsync();
await TripScheduleChecks.RunAsync();
await CompletedTripChecks.RunAsync();

static TripDTO Trip(int localId, int remoteId, string destination, DateTime start) => new()
{
    LocalTripId = localId, RemoteTripId = remoteId, LocalUserId = "local", RemoteUserId = "remote",
    Destination = destination, StartDate = start, EndDate = start.AddDays(3),
    IsActive = start >= DateTime.Today
};
static void Check(bool passed, string message)
{
    if (!passed) throw new Exception(message);
    Console.WriteLine($"PASS: {message}");
}

sealed class Details(ITripListPreloadService preload)
    : StartViewModel(null!, null!, new Session(), null!, null!, null!, preload)
{
    public Task SelectAsync(TripDTO trip) => OnNavigatedToAsync(new Dictionary<string, object>
    {
        ["selectedTrip"] = new TripViewModel(trip)
    });
}
sealed class Session : ISessionService
{
    public string LocalUserId => "local";
    public bool GlobalIsBusy { get; set; }
}
sealed class Preload : ITripListPreloadService
{
    public IReadOnlyList<TripWithStats> Trips { get; set; } = [];
    public bool Fail { get; set; }
    public bool ForceRefresh { get; private set; }
    public Task<IReadOnlyList<TripWithStats>> GetTripsAsync(bool forceRefresh = false)
    {
        ForceRefresh = forceRefresh;
        return Fail ? Task.FromException<IReadOnlyList<TripWithStats>>(new IOException("Offline"))
            : Task.FromResult(Trips);
    }
    public Task PrefetchAsync() => Task.CompletedTask;
    public void Invalidate() { }
}

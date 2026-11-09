using Packo.Models.DTO;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;
using System.ComponentModel;

var nearest = Trip(1, 10, "Nearest", DateTime.Today);
var later = Trip(2, 20, "Selected", DateTime.Today.AddDays(60));
var past = Trip(3, 30, "Past", DateTime.Today.AddDays(-60));
var preload = new Preload { Trips = [new(nearest, "0 / 4"), new(later, "2 / 8"), new(past, "1 / 3")] };
var localTrips = new LocalTripLookup();
var vm = new Details(preload, localTrips);
await vm.SelectAsync(later);
await vm.OnAppearingAsync();
Check(vm.TripDestination == "Selected", "Details show the selected trip instead of the nearest trip");
Check(vm.Title == "TripDetails", "Details use a stable title");

await vm.EditTripCommand.ExecuteAsync(null);
Check(Shell.Current.Route == "TripSetupPage" && ReferenceEquals(Shell.Current.Query!["editTrip"], later),
    "Editing opens the selected trip");

var updated = Trip(2, 20, "Updated destination", later.StartDate!.Value);
preload.Trips = [new(nearest, "0 / 4"), new(updated, "6 / 8")];
await vm.OnAppearingAsync();
Check(vm.TripDestination == "Updated destination",
    "Returning from editing or packing reloads the same trip's details");

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
Check(vm.TripDestination == "Offline trip",
    "Unsynced trips use both the local ID and owner when selecting details");

preload.Trips = [new(nearest, "0 / 4")];
localTrips.Trip = offline;
await vm.OnAppearingAsync();
Check(ReferenceEquals(vm.Trip?.TripModel, offline),
    "A just-created local trip opens in details before it appears in the trip feed");
localTrips.Trip = null;
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

sealed class Details(ITripListPreloadService preload, ITripRepository? localTrips = null)
    : StartViewModel(null!, null!, new Session(), null!, localTrips ?? new LocalTripLookup(), null!, preload)
{
    public Task SelectAsync(TripDTO trip) => OnNavigatedToAsync(new Dictionary<string, object>
    {
        ["trip"] = trip
    });
}
sealed class LocalTripLookup : ITripRepository
{
    public TripDTO? Trip { get; set; }
    public event Action<TripDTO, string>? TripChanged { add { } remove { } }
    public Task DeleteTripAsync(TripDTO trip) => Task.CompletedTask;
    public Task<TripDTO?> GetLocalTripAsync(int localTripId, string localUserId) =>
        Task.FromResult(Trip?.LocalTripId == localTripId && Trip.LocalUserId == localUserId ? Trip : null);
}
sealed class Session : ISessionService, INotifyPropertyChanged
{
    public string LocalUserId => "local";
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _globalIsBusy;
    public bool GlobalIsBusy
    {
        get => _globalIsBusy;
        set
        {
            _globalIsBusy = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalIsBusy)));
        }
    }
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

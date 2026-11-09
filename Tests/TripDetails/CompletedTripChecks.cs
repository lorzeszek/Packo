using CommunityToolkit.Mvvm.Messaging;
using Packo.Helpers;
using Packo.Messages;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Local;
using Packo.Repositories.Supabase;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;
using SQLite;

internal static class CompletedTripChecks
{
    public static async Task RunAsync()
    {
        var service = new SupabaseService();
        var remote = new SupabaseTripRepository(service);
        var selected = Trip(1, "Edited trip", DateTime.Today.AddDays(1), active: true);
        selected.StartDate = DateTime.Today.AddDays(-1);
        var completed = Trip(2, "Already completed", DateTime.Today, active: false);
        var trash = Trip(3, "Trash", DateTime.Today, active: false);
        trash.IsInTrash = true;
        var staleFlag = Trip(4, "Upcoming with stale flag", DateTime.Today.AddDays(5), active: false);
        service.Client.TripRows.AddRange([Mappers.MapToTripSupabase(selected), Mappers.MapToTripSupabase(completed),
            Mappers.MapToTripSupabase(trash), Mappers.MapToTripSupabase(staleFlag)]);
        service.Client.StatsJson = "[{\"tripid\":1,\"ispackedcount\":2,\"isnotpackedcount\":3}]";

        var loaded = await remote.GetActiveTripsWithStatsAsync();
        Check(loaded.Any(row => row.Trip.RemoteTripId == 2) && loaded.Any(row => row.Trip.RemoteTripId == 4),
            "The real Supabase list query includes completed trips and other trips with IsActive=false");
        Check(loaded.All(row => row.Trip.RemoteTripId != 3), "The Supabase list still excludes trashed trips");
        var otherDeviceTrip = await remote.GetTripAsync(new TripDTO
        {
            RemoteTripId = completed.RemoteTripId, LocalTripId = selected.LocalTripId, LocalUserId = selected.LocalUserId
        });
        Check(otherDeviceTrip?.RemoteTripId == completed.RemoteTripId && otherDeviceTrip.EndDate == completed.EndDate,
            "Refreshing the schedule for AI uses the cloud trip ID even when local identities differ");

        var preload = new TripListPreloadService(remote);
        var details = new Details(preload);
        await details.SelectAsync(selected);
        await details.OnAppearingAsync();
        Check(details.HasTrip && details.TripDestination == "Edited trip", "Details are loaded before editing the end time");
        var edit = new TripSetupViewModel(null!, null!, new Session(), null!, remote, null!, null!);
        edit.ApplyQueryAttributes(new Dictionary<string, object> { ["editTrip"] = selected });
        edit.EndDate = DateTime.Today;
        edit.EndTime = TimeSpan.Zero;
        await edit.CreateTripCommand.ExecuteAsync(null);
        Check(Shell.Current.Route == "..", "Save returns to the existing details page instead of navigating to the Trips root");
        Check(!service.Client.TripRows.Single(row => row.Id == 1).IsActive,
            "Saving an end time in the past correctly stores IsActive=false without deleting the row");

        // SyncTripRepository publishes this message after saving locally and remotely.
        WeakReferenceMessenger.Default.Send(new TripDataChangedMessage());
        await details.OnAppearingAsync();
        Check(details.HasTrip && !details.ShowTripUnavailable && details.Trip!.IsCompleted
            && details.Trip.StatusLabel == "PastTrip",
            "Returning after Save keeps the selected trip details and completed badge");

        var session = new Session();
        var list = new TripListViewModel(null!, null!, session, null!, remote, null!, preload, new Weather());
        await list.OnAppearingAsync();
        Check(list.Trips.Any(trip => trip.TripModel.RemoteTripId == 1 && trip.IsCompleted),
            "The trip whose end time was edited into the past stays on the Trips list");
        Check(list.Trips[0].TripModel.RemoteTripId == 4,
            "Upcoming trips remain above completed trips after the list reloads");

        var tile = list.Trips.Single(trip => trip.TripModel.RemoteTripId == 1);
        var detailsCommand = list.OpenTripDetailsCommand;
        await detailsCommand.ExecuteAsync(tile);
        Check(Shell.Current.Route == StartViewModel.Route && ReferenceEquals(Shell.Current.Query?["trip"], tile.TripModel),
            "The shared tile and suitcase command opens the selected trip details");

        var propertiesChanged = new List<string?>();
        tile.PropertyChanged += (_, args) => propertiesChanged.Add(args.PropertyName);
        var loadingStarted = false;
        list.PropertyChanged += (_, args) => loadingStarted |= args.PropertyName == nameof(list.IsLoading) && list.IsLoading;
        await list.OnNavigatedFromAsync(new Dictionary<string, object>());
        session.GlobalIsBusy = true;
        WeakReferenceMessenger.Default.Send(new PackingProgressChangedMessage(1, 1, "local", "3 / 5"));
        await Shell.Current.GoToAsync("//TripList");
        await list.OnAppearingAsync();
        var otherTile = list.Trips[0];
        var suitcaseEnabled = detailsCommand.CanExecute(otherTile);
        detailsCommand.CanExecuteChanged += (_, _) => suitcaseEnabled = detailsCommand.CanExecute(otherTile);
        Check(!suitcaseEnabled, "The suitcase is disabled while packing is still loading");
        session.GlobalIsBusy = false;
        Check(suitcaseEnabled, "Finishing the load after returning to Trips re-enables the suitcase buttons");
        await detailsCommand.ExecuteAsync(otherTile);
        Check(Shell.Current.Route == StartViewModel.Route && ReferenceEquals(Shell.Current.Query?["trip"], otherTile.TripModel),
            "A different trip's details can be opened after returning to Trips");

        Check(list.ShowTrips && !loadingStarted && list.Trips.Contains(tile) && tile.PackingSummary == "3 / 5"
            && propertiesChanged.SequenceEqual([nameof(TripViewModel.PackingSummary)]),
            "Returning from packing updates only the existing tile's counter without entering the loading state");

        session.GlobalIsBusy = true;
        Check(!suitcaseEnabled, "Starting another load disables the suitcase buttons again");
        await list.OnNavigatedFromAsync(new Dictionary<string, object>());
        session.GlobalIsBusy = false;
        await Shell.Current.GoToAsync("//TripList");
        await list.OnAppearingAsync();
        Check(suitcaseEnabled, "Returning to Trips also refreshes the buttons when the load finished offscreen");
        await detailsCommand.ExecuteAsync(otherTile);
        Check(Shell.Current.Route == StartViewModel.Route && ReferenceEquals(Shell.Current.Query?["trip"], otherTile.TripModel),
            "The same trip's details can be reopened after returning to Trips");

        WeakReferenceMessenger.Default.Send(new TripDataChangedMessage());
        await list.OnAppearingAsync();
        Check(loadingStarted && list.Trips.Single(trip => trip.TripModel.RemoteTripId == 1).PackingSummary == "2 / 5",
            "A trip data change still reloads the list and its cloud stats");

        SQLitePCL.Batteries_V2.Init();
        var path = Path.Combine(Path.GetTempPath(), $"packo-completed-trips-{Guid.NewGuid():N}.db");
        var db = new SQLiteAsyncConnection(path);
        try
        {
            await db.CreateTableAsync<SQLiteTrip>();
            await db.CreateTableAsync<SQLitePackingItem>();
            var local = new LocalTripRepository(db, new Session());
            await local.AddTripAsync(completed);
            await local.AddTripAsync(trash);
            var otherUser = Trip(5, "Another user", DateTime.Today, active: false);
            otherUser.LocalUserId = "other";
            await local.AddTripAsync(otherUser);
            var offline = await local.GetActiveTripsWithStatsAsync();
            Check(offline.Count == 1 && offline[0].Trip.RemoteTripId == 2 && !offline[0].Trip.IsActive,
                "SQLite also keeps completed trips visible while excluding trash and other users");
        }
        finally
        {
            await db.CloseAsync();
            File.Delete(path);
        }
    }

    private static TripDTO Trip(int id, string name, DateTime end, bool active) => new()
    {
        RemoteTripId = id, LocalTripId = id, LocalUserId = "local", RemoteUserId = "remote",
        Destination = name, StartDate = end.AddDays(-1), EndDate = end, IsActive = active
    };
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }
    private sealed class SupabaseService : ISupabaseService { public AuthClient Client { get; } = new(); }
    private sealed class Weather : IWeatherPreloadService { public void Invalidate() { } }
    private sealed class Details(ITripListPreloadService preload)
        : StartViewModel(null!, null!, new Session(), null!, null!, null!, preload)
    {
        public Task SelectAsync(TripDTO trip) => OnNavigatedToAsync(new Dictionary<string, object>
        {
            ["trip"] = trip
        });
    }
}

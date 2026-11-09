using Packo.Models.DTO;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using Packo.ViewModels;

internal static class TripDeletionChecks
{
    public static async Task RunAsync()
    {
        var repository = new Trips();
        var session = new DeletionSession();
        var vm = new TripListViewModel(null!, null!, session, null!, repository, null!, new Preload(), new Weather());
        var trip = new TripViewModel(new TripDTO { Destination = "Rome", LocalTripId = 1, LocalUserId = "local" });
        var other = new TripViewModel(new TripDTO { Destination = "Paris", LocalTripId = 2, LocalUserId = "local" });
        vm.Trips.AddRange([trip, other]);

        ConfirmPopup.Confirm = () => Task.FromResult(false);
        await vm.TrashTripCommand.ExecuteAsync(trip);
        Check(repository.DeleteCalls == 0 && vm.Trips.Contains(trip),
            "Cancelling trip deletion leaves the trip untouched and never calls the repository");
        Check(ConfirmPopup.ConfirmationMessage!.Contains("Rome"),
            "The confirmation identifies the trip by destination");
        Check(ReferenceEquals(vm.DeleteTripCommand, vm.TrashTripCommand),
            "Both deletion command names share the same confirmation flow");

        var answer = new TaskCompletionSource<bool>();
        ConfirmPopup.Confirm = () => answer.Task;
        var pending = vm.TrashTripCommand.ExecuteAsync(trip);
        Check(repository.DeleteCalls == 0 && !vm.TrashTripCommand.CanExecute(other),
            "Deletion waits for an answer and disables repeated clicks while the dialog is open");
        answer.SetResult(true);
        await pending;
        Check(repository.DeleteCalls == 1 && ReferenceEquals(repository.DeletedTrip, trip.TripModel)
            && !vm.Trips.Contains(trip) && vm.Trips.Contains(other),
            "Confirming deletes exactly the selected trip and immediately removes its tile");

        repository.Fail = true;
        ConfirmPopup.Confirm = () => Task.FromResult(true);
        await vm.TrashTripCommand.ExecuteAsync(other);
        Check(vm.Trips.Contains(other) && ConfirmPopup.LastMessage == "DeleteTripError",
            "A failed deletion keeps the trip visible and reports an error");

        var prompts = ConfirmPopup.ConfirmationCount;
        var calls = repository.DeleteCalls;
        session.GlobalIsBusy = true;
        await vm.TrashTripCommand.ExecuteAsync(other);
        session.GlobalIsBusy = false;
        await vm.TrashTripCommand.ExecuteAsync(null);
        Check(repository.DeleteCalls == calls && ConfirmPopup.ConfirmationCount == prompts,
            "Busy or missing selections cannot open a dialog or delete a trip");
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }

    private sealed class Trips : ITripRepository
    {
        public event Action<TripDTO, string>? TripChanged { add { } remove { } }
        public int DeleteCalls { get; private set; }
        public TripDTO? DeletedTrip { get; private set; }
        public bool Fail { get; set; }
        public Task DeleteTripAsync(TripDTO trip)
        {
            DeleteCalls++;
            if (Fail) throw new IOException("Simulated deletion failure");
            DeletedTrip = trip;
            return Task.CompletedTask;
        }
    }
    private sealed class DeletionSession : ISessionService
    {
        public string LocalUserId => "local";
        public bool GlobalIsBusy { get; set; }
    }
    private sealed class Preload : ITripListPreloadService
    {
        public Task<IReadOnlyList<TripWithStats>> GetTripsAsync(bool forceRefresh = false)
            => Task.FromResult<IReadOnlyList<TripWithStats>>([]);
        public Task PrefetchAsync() => Task.CompletedTask;
        public void Invalidate() { }
    }
    private sealed class Weather : IWeatherPreloadService { public void Invalidate() { } }
}

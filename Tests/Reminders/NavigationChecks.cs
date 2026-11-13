using Packo.Helpers;
using Packo.Models.DTO;

internal static class NavigationChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); count++; }
        var ready = false;
        var account = "account-a";
        var opened = new List<TripDTO>();
        var unavailable = 0;
        var lookups = 0;
        var tripA = new TripDTO { LocalUserId = account, LocalTripId = 1, RemoteTripId = 10 };
        var tripB = new TripDTO { LocalUserId = account, LocalTripId = 2, RemoteTripId = 20 };
        Func<ReminderTripTarget, Task<TripDTO?>> resolver = target => Task.FromResult<TripDTO?>(target.RemoteTripId == 10 ? tripA : tripB);
        var queue = new ReminderNavigationQueue(() => ready, () => account,
            target => { lookups++; return resolver(target); }, trip => { opened.Add(trip); return Task.CompletedTask; },
            () => { unavailable++; return Task.CompletedTask; });
        var targetA = new ReminderTripTarget(account, 1, 10);
        var targetB = new ReminderTripTarget(account, 2, 20);
        queue.Receive(targetA);
        await queue.ProcessAsync();
        Check(queue.HasPending && opened.Count == 0 && lookups == 0, "A cold-start notification waits for the application and session to be ready");
        ready = true;
        await queue.ProcessAsync();
        Check(!queue.HasPending && opened.Single() == tripA, "A notification opens the trip it belongs to after startup");
        await queue.ProcessAsync();
        Check(opened.Count == 1, "Lifecycle events do not replay a consumed notification tap");
        queue.Receive(targetB);
        await queue.ProcessAsync();
        Check(opened.Count == 2 && opened.Last() == tripB, "A warm notification can open another trip");
        var beforeLookup = lookups;
        queue.Receive(new ReminderTripTarget("another-account", 1, 10));
        await queue.ProcessAsync();
        Check(lookups == beforeLookup && opened.Count == 2 && !queue.HasPending, "A notification from a previous account cannot open its trip");
        resolver = _ => Task.FromResult<TripDTO?>(null);
        queue.Receive(targetA); await queue.ProcessAsync();
        Check(unavailable == 1 && opened.Count == 2, "A deleted or inaccessible trip shows an unavailable result");
        resolver = _ => Task.FromResult<TripDTO?>(tripB);
        queue.Receive(targetA); await queue.ProcessAsync();
        Check(unavailable == 2 && opened.Count == 2, "An overlapping local trip ID cannot substitute a different remote trip");
        tripA.IsInTrash = true;
        resolver = _ => Task.FromResult<TripDTO?>(tripA);
        queue.Receive(targetA); await queue.ProcessAsync();
        Check(unavailable == 3, "A trip in trash is not opened from an old notification");
        tripA.IsInTrash = false;
        Check(new ReminderTripTarget(account, 1, 0).Matches(tripA), "A local-only notification still matches after its trip gets a remote ID");
        queue.Receive(new ReminderTripTarget(account, -1, 10));
        Check(!queue.HasPending, "Invalid notification identifiers are ignored");
        var deferred = new TaskCompletionSource<TripDTO?>();
        resolver = _ => deferred.Task;
        queue.Receive(targetA);
        var processing = queue.ProcessAsync();
        account = "account-b";
        deferred.SetResult(tripA);
        await processing;
        Check(opened.Count == 2 && !queue.HasPending, "Switching accounts during lookup prevents navigation to cached private data");
        account = "account-a";
        deferred = new TaskCompletionSource<TripDTO?>();
        resolver = _ => deferred.Task;
        queue.Receive(targetA); processing = queue.ProcessAsync(); ready = false;
        deferred.SetResult(tripA); await processing;
        Check(queue.HasPending && opened.Count == 2, "Navigation defers if a modal or background transition begins during lookup");
        ready = true;
        resolver = _ => Task.FromResult<TripDTO?>(tripA);
        await queue.ProcessAsync();
        Check(opened.Count == 3 && opened.Last() == tripA, "The pending trip opens when the application becomes ready again");
        deferred = new TaskCompletionSource<TripDTO?>();
        resolver = target => target.RemoteTripId == 10 ? deferred.Task : Task.FromResult<TripDTO?>(tripB);
        queue.Receive(targetA); processing = queue.ProcessAsync();
        queue.Receive(targetB); await queue.ProcessAsync(); deferred.SetResult(tripA); await processing;
        Check(opened.Count == 4 && opened.Last() == tripB && !queue.HasPending, "The latest tap wins when another notification is selected during lookup");
        Console.WriteLine($"{count} notification navigation checks passed.");
    }
}

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
        var presented = new List<ReminderTripTarget>();
        var steps = new List<string>();
        Func<ReminderTripTarget, TripDTO, Task<bool>> prompt = (_, _) => Task.FromResult(true);
        Func<Task> list = () => Task.CompletedTask;
        var tripA = new TripDTO { LocalUserId = account, LocalTripId = 1, RemoteTripId = 10 };
        var tripB = new TripDTO { LocalUserId = account, LocalTripId = 2, RemoteTripId = 20 };
        Func<ReminderTripTarget, Task<TripDTO?>> resolver = target => Task.FromResult<TripDTO?>(target.RemoteTripId == 10 ? tripA : tripB);
        var queue = new ReminderNavigationQueue(() => ready, () => account,
            target => { lookups++; return resolver(target); },
            async () => { steps.Add("list"); await list(); },
            (target, trip) => { steps.Add("prompt"); presented.Add(target); return prompt(target, trip); },
            trip => { steps.Add("details"); opened.Add(trip); return Task.CompletedTask; },
            () => { unavailable++; return Task.CompletedTask; });
        var targetA = new ReminderTripTarget(account, 1, 10, ReminderId: "reminder-a", Title: "Documents", Body: "Bring passports and tickets");
        var targetB = new ReminderTripTarget(account, 2, 20);
        queue.Receive(targetA);
        await queue.ProcessAsync();
        Check(queue.HasPending && opened.Count == 0 && lookups == 0, "A cold-start notification waits for the application and session to be ready");
        ready = true;
        await queue.ProcessAsync();
        Check(!queue.HasPending && opened.Single() == tripA, "A notification opens the trip it belongs to after startup");
        Check(steps.SequenceEqual(new[] { "list", "prompt", "details" }), "A tap opens the Trips list, then the popup, and only then confirmed details");
        Check(presented.Single().Title == "Documents" && presented.Single().Body == "Bring passports and tickets",
            "The popup receives the title and full body carried by the delivered notification");
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
        prompt = (_, _) => Task.FromResult(false);
        steps.Clear();
        queue.Receive(targetB); await queue.ProcessAsync();
        Check(opened.Count == 4 && steps.SequenceEqual(new[] { "list", "prompt" }) && !queue.HasPending,
            "Close dismisses the reminder and stays on the Trips list without opening details");
        var confirmation = new TaskCompletionSource<bool>();
        prompt = (_, _) => confirmation.Task;
        queue.Receive(targetA); processing = queue.ProcessAsync();
        account = "account-b"; confirmation.SetResult(true); await processing;
        Check(opened.Count == 4 && !queue.HasPending, "Changing accounts while the popup is open prevents its details from opening");
        account = "account-a";
        confirmation = new TaskCompletionSource<bool>();
        prompt = (target, _) => target.RemoteTripId == 10 ? confirmation.Task : Task.FromResult(true);
        queue.Receive(targetA); processing = queue.ProcessAsync(); queue.Receive(targetB);
        confirmation.SetResult(true); await processing;
        Check(opened.Count == 5 && opened.Last() == tripB,
            "A newer notification tap takes priority over an older popup's confirmation");
        confirmation = new TaskCompletionSource<bool>(); prompt = (_, _) => confirmation.Task;
        queue.Receive(targetA); processing = queue.ProcessAsync(); ready = false;
        confirmation.SetResult(true); await processing;
        var promptsBeforeResume = presented.Count;
        Check(queue.HasPending && opened.Count == 5, "Confirmed details wait while the application is inactive");
        ready = true; await queue.ProcessAsync();
        Check(opened.Count == 6 && opened.Last() == tripA && presented.Count == promptsBeforeResume,
            "Confirmed details resume without showing the reminder popup again");
        confirmation = new TaskCompletionSource<bool>();
        queue.Receive(targetA); processing = queue.ProcessAsync();
        resolver = _ => Task.FromResult<TripDTO?>(null);
        confirmation.SetResult(true); await processing;
        Check(opened.Count == 6 && unavailable == 4, "Trip access is checked again before opening confirmed details");
        resolver = _ => Task.FromResult<TripDTO?>(tripA);
        prompt = (_, _) => Task.FromResult(false);
        list = () => { ready = false; return Task.CompletedTask; };
        var beforeListReady = presented.Count;
        queue.Receive(targetA); await queue.ProcessAsync();
        Check(queue.HasPending && presented.Count == beforeListReady, "The popup waits if the Trips page is still loading after navigation");
        list = () => Task.CompletedTask; ready = true; await queue.ProcessAsync();
        Check(!queue.HasPending && presented.Count == beforeListReady + 1, "The popup appears once the Trips page is ready");
        prompt = (target, _) => { ready = false; queue.Receive(target); return Task.FromResult(false); };
        queue.Receive(targetA); await queue.ProcessAsync();
        Check(queue.HasPending && opened.Count == 6, "Reading legacy notification text can defer presentation if the app becomes inactive");
        ready = true; prompt = (_, _) => Task.FromResult(false); await queue.ProcessAsync();
        Check(!queue.HasPending && opened.Count == 6, "A deferred legacy popup resumes without automatically opening details");
        Console.WriteLine($"{count} notification navigation checks passed.");
    }
}

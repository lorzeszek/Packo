using System.Text.Json;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using SQLite;

internal static class ServiceChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            Console.WriteLine("PASS " + description); count++;
        }
        var path = Path.Combine(Path.GetTempPath(), "packo-reminders-" + Guid.NewGuid() + ".db3");
        var db = new SQLiteAsyncConnection(path);
        try
        {
            await db.CreateTableAsync<SQLiteTrip>();
            await db.CreateTableAsync<Packo.Repositories.Models.SQLitePendingTripChange>();
            var session = new Session();
            var remote = new Remote();
            var scheduler = new Scheduler();
            var service = new TripReminderService(db, session, remote, new Trips(), scheduler);
            var novemberTrip = new TripDTO { StartDate = new DateTime(2026, 11, 14), EndDate = new DateTime(2026, 11, 20), TimeZoneId = "Europe/Warsaw" };
            var novemberDraft = service.New(novemberTrip, new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero));
            Check(novemberDraft.Date == new DateTime(2026, 11, 13), "A new reminder starts today, even when the trip starts tomorrow");
            var calendarJson = JsonSerializer.Serialize(new TripReminder { Date = DateTime.SpecifyKind(new DateTime(2026, 11, 13), DateTimeKind.Local) });
            var calendarPayload = TripReminderService.ReadDefinitionPayload(calendarJson);
            var calendarWireJson = Newtonsoft.Json.JsonConvert.SerializeObject(calendarPayload,
                new Newtonsoft.Json.JsonSerializerSettings { DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling.Utc });
            Check(calendarPayload["Date"]!.Type == Newtonsoft.Json.Linq.JTokenType.String && calendarWireJson.Contains("\"Date\":\"2026-11-13\""),
                "A selected calendar date stays on November 13 even if the RPC serializer uses UTC");
            var localTrip = new SQLiteTrip { LocalUserId = "local-a", RemoteUserId = "a", Destination = "Test",
                StartDate = DateTime.Today.AddDays(5), EndDate = DateTime.Today.AddDays(10), TimeZoneId = "Europe/Warsaw" };
            await db.InsertAsync(localTrip);
            var trip = Packo.Repositories.Local.LocalTripRepository.ToTrip(localTrip);
            var reminder = service.New(trip);
            reminder.Title = "Test"; reminder.Body = "Body"; reminder.Date = DateTime.Today.AddDays(6);
            reminder.Repeat = ReminderRepeat.Daily;
            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            await service.SaveAsync(trip, reminder);
            Check((await service.GetAsync(trip)).Single().Pending && scheduler.Items.Count == 1, "An offline reminder is persisted and scheduled without a server");
            await service.SetMutedAsync(trip, reminder.Id, true);
            Check(scheduler.Items.Count == 0 && (await service.GetAsync(trip)).Single().Muted, "Individual muting immediately cancels local delivery");
            await service.SetMutedAsync(trip, reminder.Id, false);
            Check(scheduler.Items.Count == 1, "Unmuting restores the upcoming local schedule");
            var group = service.New(trip); group.Title = "Group"; group.Body = "Shared";
            group.Date = reminder.Date; group.Audience = ReminderAudience.All;
            await service.SaveAsync(trip, group);
            await service.SetMutedAsync(trip, "*", true);
            Check(scheduler.Items.Count == 1 && scheduler.Items[0].Definition.Id == reminder.Id, "Trip-wide group muting preserves personal reminders");
            await service.SetMutedAsync(trip, "*", false);
            session.LocalUserId = "local-b"; session.UserId = "b";
            await service.RefreshAsync();
            Check(scheduler.Account == "local-b" && scheduler.Items.Count == 0, "Switching accounts clears another account's active schedule");
            Check((await service.GetAsync(trip)).Count == 0, "Another account cannot read cached reminder text");
            session.LocalUserId = "local-a"; session.UserId = "a";
            await service.RefreshAsync();
            Check(scheduler.Items.Count == 2, "Returning to an account restores only its own schedules");
            localTrip.EndDate = DateTime.Today.AddDays(1); await db.UpdateAsync(localTrip);
            await service.RefreshAsync();
            Check(scheduler.Items.Count == 0, "Offline shortening of a trip cancels now-invalid alarms");
            localTrip.EndDate = DateTime.Today.AddDays(10); localTrip.RemoteTripId = 100; await db.UpdateAsync(localTrip);
            trip.RemoteTripId = 100;
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            var serverEntries = new List<object>();
            remote.Client.Respond = (name, args) =>
            {
                if (name == "save_trip_reminder")
                {
                    Check(args["p_definition"] is Newtonsoft.Json.Linq.JObject, "RPC sends a JSON object understood by the Supabase serializer");
                    var value = JsonSerializer.Deserialize<TripReminder>(args["p_definition"].ToString()!)!;
                    value.Version = 1;
                    serverEntries.Add(new { Definition = value, Trip = trip });
                    return Task.FromResult(new Response(JsonSerializer.Serialize(new { Status = "ok", Definition = value })));
                }
                if (name == "mute_trip_reminder") return Task.FromResult(new Response("true"));
                return Task.FromResult(new Response(JsonSerializer.Serialize(new { Entries = serverEntries, Preferences = Array.Empty<object>() })));
            };
            await service.RefreshAsync();
            Check((await service.GetAsync(trip)).All(x => !x.Pending && x.Definition.Version == 1), "Reconnection pushes local drafts and merges their canonical server versions");
            var dateEdit = (await service.GetAsync(trip)).First(x => x.Definition.Id == reminder.Id).Definition.Copy();
            dateEdit.Date = dateEdit.Date.AddDays(-1);
            var expectedDate = dateEdit.Date;
            remote.Client.Respond = (name, args) =>
            {
                if (name == "save_trip_reminder")
                {
                    var updated = JsonSerializer.Deserialize<TripReminder>(args["p_definition"].ToString()!)!;
                    updated.Version++;
                    dateEdit = updated;
                    return Task.FromResult(new Response(JsonSerializer.Serialize(new { Status = "ok", Definition = updated })));
                }
                return Task.FromResult(new Response(JsonSerializer.Serialize(new { Entries = new[] { new { Definition = dateEdit, Trip = trip }, new { Definition = group, Trip = trip } }, Preferences = Array.Empty<object>() })));
            };
            await service.SaveAsync(trip, dateEdit);
            await service.RefreshAsync();
            Check((await service.GetAsync(trip)).First(x => x.Definition.Id == reminder.Id).Definition.Date == expectedDate,
                "Moving a saved reminder one day earlier survives the server acknowledgment and another synchronization");
            remote.Client.Respond = (_, _) => throw new IOException("Temporary network failure");
            await service.RefreshAsync();
            Check(scheduler.Items.Count == 2 && service.SyncError == "ReminderSyncFailed", "A failed server request preserves downloaded offline schedules");
            var saved = (await service.GetAsync(trip)).First(x => x.Definition.Id == reminder.Id).Definition.Copy();
            var otherVersion = saved.Copy(); otherVersion.Version = saved.Version + 1; otherVersion.Title = "Changed elsewhere";
            remote.Client.Respond = (name, args) => Task.FromResult(new Response(name switch
            {
                "save_trip_reminder" => JsonSerializer.Serialize(new { Status = "conflict", Definition = otherVersion }),
                "trip_reminders_snapshot" => JsonSerializer.Serialize(new { Entries = new[] { new { Definition = otherVersion, Trip = trip } }, Preferences = Array.Empty<object>() }),
                _ => "true"
            }));
            saved.Title = "Offline edit";
            var conflictReported = false;
            try { await service.SaveAsync(trip, saved); }
            catch (InvalidOperationException ex) when (ex.Message == "ReminderConflict") { conflictReported = true; }
            var conflicted = (await service.GetAsync(trip)).Single();
            Check(conflictReported && conflicted.Error == "ReminderConflict" && conflicted.Definition.Title == "Changed elsewhere" && !conflicted.Pending,
                "Conflicting edits expose the newer version instead of overwriting it");
            var earlierDate = conflicted.Definition.Copy();
            earlierDate.Date = earlierDate.Date.AddDays(-1);
            var invalidReported = false;
            remote.Client.Respond = (name, args) => Task.FromResult(new Response(name == "save_trip_reminder"
                ? "{\"Status\":\"invalid\"}" : JsonSerializer.Serialize(new { Entries = new[] { new { Definition = otherVersion, Trip = trip } }, Preferences = Array.Empty<object>() })));
            try { await service.SaveAsync(trip, earlierDate); }
            catch (InvalidOperationException ex) when (ex.Message == "ReminderScheduleError") { invalidReported = true; }
            Check(invalidReported, "A server-rejected date edit is reported to the editor instead of appearing successfully saved");
            var rejected = service.New(trip); rejected.Title = "Rejected draft"; rejected.Body = "Check"; rejected.Date = DateTime.Today.AddDays(6);
            remote.Client.Respond = (name, args) => Task.FromResult(new Response(name == "save_trip_reminder"
                ? "{\"Status\":\"denied\"}" : "{\"Entries\":[],\"Preferences\":[]}"));
            var denialReported = false;
            try { await service.SaveAsync(trip, rejected); }
            catch (InvalidOperationException ex) when (ex.Message == "ReminderDenied") { denialReported = true; }
            var rejectedEntry = (await service.GetAsync(trip)).Single();
            Check(denialReported && rejectedEntry.Error == "ReminderDenied" && !rejectedEntry.Definition.Enabled && scheduler.Items.Count == 0,
                "A rejected new draft remains visible with an explanation and cannot trigger an alarm");
            var ownerTrip = new TripDTO { IsJoinedTrip = false };
            var memberTrip = new TripDTO { IsJoinedTrip = true };
            var someoneElse = new TripReminder { AuthorId = "b", Audience = ReminderAudience.All };
            Check(service.CanEdit(someoneElse, ownerTrip) && !service.CanEdit(someoneElse, memberTrip), "Only the author and trip owner can edit a group reminder");
            someoneElse.Audience = ReminderAudience.Self;
            Check(!service.CanEdit(someoneElse, ownerTrip), "Another participant's personal reminder stays private from owner editing");
            remote.Client.Respond = (_, _) => Task.FromResult(new Response(JsonSerializer.Serialize(new
                { Entries = new[] { new { Definition = otherVersion, Trip = trip } }, Preferences = Array.Empty<object>() })));
            await service.RefreshAsync();
            remote.Client.Respond = (name, _) => Task.FromResult(new Response(name == "save_trip_reminder"
                ? "{\"Status\":\"denied\"}" : "{\"Entries\":[],\"Preferences\":[]}"));
            var revokedSaveReported = false;
            try { await service.SaveAsync(trip, otherVersion.Copy()); }
            catch (InvalidOperationException ex) when (ex.Message == "ReminderDenied") { revokedSaveReported = true; }
            Check(revokedSaveReported, "Losing access during an edit cannot be reported as a successful save");
            remote.Client.Respond = (_, _) => Task.FromResult(new Response("{\"Entries\":[],\"Preferences\":[]}"));
            await service.RefreshAsync();
            Check((await service.GetAsync(trip)).All(x => x.Definition.Id == rejected.Id) && scheduler.Items.Count == 0,
                "A successful snapshot removes revoked, deleted and no-longer-addressed server reminders");
            Console.WriteLine($"{count} reminder storage and sync checks passed.");
        }
        finally { await db.CloseAsync(); File.Delete(path); }
    }

    private sealed class Session : ISessionService
    {
        public string? LocalUserId { get; set; } = "local-a";
        public string? UserId { get; set; } = "a";
        public bool IsAuthenticated => true;
        public TestUser? User => null;
    }
    private sealed class Remote : ISupabaseService
    {
        public FakeClient Client { get; } = new();
        public Task EnsureRealtimeConnectedAsync() => Task.CompletedTask;
    }
    private sealed class Trips : ITripRepository { public Task SyncPendingChangesAsync() => Task.CompletedTask; }
    private sealed class Scheduler : IReminderScheduler
    {
        public string Account { get; private set; } = "";
        public IReadOnlyList<ScheduledReminder> Items { get; private set; } = [];
        public bool IsSupported => true;
        public bool HasExactTiming => true;
        public bool HasPermission => true;
        public Task<bool> RequestPermissionAsync() => Task.FromResult(true);
        public Task ReplaceAsync(string accountId, IReadOnlyList<ScheduledReminder> reminders)
        { Account = accountId; Items = reminders; return Task.CompletedTask; }
        public void OpenTimingSettings() { }
    }
}

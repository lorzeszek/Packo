using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Helpers;
using Packo.Messages;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Local;
using Packo.Services.Interfaces;
using SQLite;

namespace Packo.Services;

public sealed record ReminderEntry(TripReminder Definition, bool Muted, bool Pending, string? Error,
    bool IndividuallyMuted = false, bool GroupMuted = false);

public sealed class TripReminderService
{
    private readonly SQLiteAsyncConnection _db;
    private readonly ISessionService _session;
    private readonly ISupabaseService _supabase;
    private readonly ITripRepository _trips;
    private readonly IReminderScheduler _scheduler;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _schemaReady;
    private string? _channelAccount;
    private readonly List<global::Supabase.Realtime.RealtimeChannel> _channels = [];
    private DateTimeOffset _lastSync;
    public string? SyncError { get; private set; }
    public event Action? Changed;
    public IReminderScheduler Scheduler => _scheduler;
    public string UserId => _session.UserId ?? _session.LocalUserId ?? "";
    public string Scope => _session.LocalUserId ?? "";

    public TripReminderService(SQLiteAsyncConnection db, ISessionService session, ISupabaseService supabase,
        ITripRepository trips, IReminderScheduler scheduler)
    {
        (_db, _session, _supabase, _trips, _scheduler) = (db, session, supabase, trips, scheduler);
        WeakReferenceMessenger.Default.Register<TripReminderService, TripDataChangedMessage>(this, (s, message) => { _ = s.RefreshSafeAsync(); });
        WeakReferenceMessenger.Default.Register<TripReminderService, PackingSyncMessage>(this, (s, m) =>
        { if (m.AccessRevoked || m.RemoteTripId == 0) _ = s.RefreshSafeAsync(); });
        WeakReferenceMessenger.Default.Register<TripReminderService, LogoutCompletedMessage>(this, (s, message) => { _ = s.ResetAfterLogoutAsync(); });
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _db.CreateTableAsync<SQLiteTripReminder>();
        await _db.CreateTableAsync<SQLiteReminderPreference>();
        _schemaReady = true;
    }

    public bool CanEdit(TripReminder reminder, TripDTO trip) => reminder.AuthorId == UserId
        || (!trip.IsJoinedTrip && reminder.Audience != ReminderAudience.Self);

    public TripReminder New(TripDTO trip, DateTimeOffset? now = null)
    {
        var zoneId = CanonicalZone(trip.TimeZoneId ?? TripSchedule.DeviceTimeZone.Id);
        var localNow = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, ReminderSchedule.Zone(zoneId)).DateTime;
        var first = localNow.Date.AddHours(18);
        if (first <= localNow || (trip.EndDate.HasValue && first > trip.EndDate.Value)) first = localNow.AddMinutes(5);
        return new TripReminder
        {
            AuthorId = UserId,
            Clock = ReminderClock.Trip,
            AuthorName = _session.User?.UserMetadata.GetValueOrDefault("full_name")?.ToString() ?? "",
            Date = DateTime.SpecifyKind(first.Date, DateTimeKind.Unspecified),
            MinuteOfDay = first.Hour * 60 + first.Minute,
            TimeZoneId = zoneId
        };
    }

    public static string CanonicalZone(string id) => TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;

    public async Task<IReadOnlyList<ReminderEntry>> GetAsync(TripDTO trip)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureSchemaAsync();
            var rows = await RowsAsync(Scope);
            var preferences = await PreferencesAsync(Scope);
            return rows.Where(x => Matches(x, trip)).Select(row => new ReminderEntry(Read(row),
                    IsMuted(preferences, row, Read(row)), row.Dirty && _session.IsAuthenticated, row.SyncError,
                    preferences.Any(p => p.Muted && p.ReminderId == row.ReminderId && Matches(p, trip)),
                    Read(row).Audience != ReminderAudience.Self && preferences.Any(p => p.Muted && p.ReminderId == "*" && Matches(p, trip))))
                .Where(x => !x.Definition.Deleted)
                .OrderBy(x => ReminderSchedule.Next(x.Definition, trip, DateTimeOffset.UtcNow, Packo.Helpers.TripSchedule.DeviceTimeZone) ?? DateTimeOffset.MaxValue)
                .ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsTripMutedAsync(TripDTO trip)
    {
        await EnsureSchemaAsync();
        return (await PreferencesAsync(Scope)).Any(x => x.ReminderId == "*" && Matches(x, trip) && x.Muted);
    }

    public async Task<HashSet<int>> ActiveLocalTripIdsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureSchemaAsync();
            var prefs = await PreferencesAsync(Scope);
            var result = new HashSet<int>();
            foreach (var row in await RowsAsync(Scope))
            {
                var reminder = Read(row);
                var trip = JsonSerializer.Deserialize<TripDTO>(row.TripJson)!;
                if (reminder.IsRecipient(UserId) && !IsMuted(prefs, row, reminder)
                    && ReminderSchedule.Next(reminder, trip, DateTimeOffset.UtcNow, Packo.Helpers.TripSchedule.DeviceTimeZone).HasValue)
                    result.Add(row.LocalTripId);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(TripDTO trip, TripReminder reminder)
    {
        var scope = Scope;
        var user = UserId;
        if (!CanEdit(reminder, trip)) throw new InvalidOperationException("ReminderDenied");
        var error = reminder.Deleted ? null : ReminderSchedule.Validate(reminder, trip, DateTimeOffset.UtcNow, Packo.Helpers.TripSchedule.DeviceTimeZone);
        if (error is not null && reminder.Enabled) throw new InvalidOperationException(error);
        await _gate.WaitAsync();
        try
        {
            await EnsureSchemaAsync();
            if (Scope != scope || UserId != user) throw new InvalidOperationException("ReminderDenied");
            var key = scope + ":" + reminder.Id;
            var previous = await _db.FindAsync<SQLiteTripReminder>(key);
            if (previous is not null && Read(previous).Version != reminder.Version)
                throw new InvalidOperationException("ReminderConflict");
            await _db.InsertOrReplaceAsync(new SQLiteTripReminder
            {
                Key = key, LocalUserId = scope, ReminderId = reminder.Id,
                LocalTripId = trip.LocalTripId, RemoteTripId = trip.RemoteTripId,
                DefinitionJson = JsonSerializer.Serialize(reminder), TripJson = JsonSerializer.Serialize(trip),
                Dirty = true, ExpectedVersion = previous?.ExpectedVersion ?? reminder.Version,
                OperationId = Guid.NewGuid().ToString()
            });
            await ScheduleAsync(scope);
        }
        finally { _gate.Release(); }
        await RefreshSafeAsync();
        await _gate.WaitAsync();
        try
        {
            if (Scope != scope || UserId != user) throw new InvalidOperationException("ReminderDenied");
            var saved = await _db.FindAsync<SQLiteTripReminder>(scope + ":" + reminder.Id);
            if (saved is null && !reminder.Deleted) throw new InvalidOperationException("ReminderDenied");
            if (saved?.SyncError is { } saveError) throw new InvalidOperationException(saveError);
        }
        finally { _gate.Release(); }
    }

    public async Task SetMutedAsync(TripDTO trip, string reminderId, bool muted)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureSchemaAsync();
            var key = (await PreferencesAsync(Scope)).FirstOrDefault(x => Matches(x, trip) && x.ReminderId == reminderId)?.Key
                ?? PreferenceKey(Scope, trip.LocalTripId, trip.RemoteTripId, reminderId);
            await _db.InsertOrReplaceAsync(new SQLiteReminderPreference
            {
                Key = key, LocalUserId = Scope, LocalTripId = trip.LocalTripId,
                RemoteTripId = trip.RemoteTripId, ReminderId = reminderId, Muted = muted, Dirty = true
            });
            await ScheduleAsync(Scope);
        }
        finally { _gate.Release(); }
        await RefreshSafeAsync();
    }

    public async Task RefreshSafeAsync(bool force = true)
    {
        try { await RefreshAsync(force); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Reminder refresh: {ex.GetType().Name}"); }
    }

    private async Task ResetAfterLogoutAsync()
    {
        await _gate.WaitAsync();
        try { await _scheduler.ReplaceAsync("", []); }
        finally { _gate.Release(); }
        await RefreshSafeAsync();
    }

    public async Task RefreshAsync(bool force = true)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureSchemaAsync();
            var scope = Scope;
            var account = UserId;
            if (string.IsNullOrWhiteSpace(scope))
            {
                // A cold Android receiver has no restored MAUI session. This is
                // not a logout: keep its persisted alarms and visible notifications.
                // Actual logout and account changes clear them explicitly.
                return;
            }
            var localTrips = (await _db.Table<SQLiteTrip>().Where(x => x.LocalUserId == scope).ToListAsync())
                .Select(LocalTripRepository.ToTrip).ToList();
            // Apply date changes, trash and local deletions even when the network is down.
            foreach (var row in await RowsAsync(scope))
            {
                var trip = localTrips.FirstOrDefault(t => Matches(row, t));
                if (trip is null) { await _db.DeleteAsync(row); continue; }
                row.LocalTripId = trip.LocalTripId;
                row.RemoteTripId = trip.RemoteTripId;
                row.TripJson = JsonSerializer.Serialize(trip);
                var definition = Read(row);
                if (definition.Version == 0 && !trip.IsJoinedTrip && definition.AuthorId != account)
                {
                    definition.AuthorId = account; // A guest draft attached to this account.
                    row.DefinitionJson = JsonSerializer.Serialize(definition);
                }
                await _db.UpdateAsync(row);
            }
            await ScheduleAsync(scope);
            if (_channelAccount != account)
            {
                SyncError = null;
                foreach (var channel in _channels) channel.Unsubscribe();
                _channels.Clear();
                _channelAccount = null;
            }
            if (_session.IsAuthenticated && Connectivity.Current.NetworkAccess == NetworkAccess.Internet
                && (force || DateTimeOffset.UtcNow - _lastSync > TimeSpan.FromSeconds(10)))
            {
                try
                {
                    await _trips.SyncPendingChangesAsync();
                    localTrips = (await _db.Table<SQLiteTrip>().Where(x => x.LocalUserId == scope).ToListAsync())
                        .Select(LocalTripRepository.ToTrip).ToList();
                    await PushAsync(scope, localTrips);
                    var response = await _supabase.Client.Rpc("trip_reminders_snapshot", new Dictionary<string, object>());
                if (Scope != scope || UserId != account) { await _scheduler.ReplaceAsync("", []); return; }
                    var snapshot = JsonSerializer.Deserialize<RemoteSnapshot>(response.Content ?? "{}")
                        ?? throw new JsonException();
                    await MergeAsync(scope, snapshot, localTrips);
                    SyncError = null;
                    _lastSync = DateTimeOffset.UtcNow;
                    await ScheduleAsync(scope);
                    await StartRealtimeAsync(account);
                }
                catch (Exception ex)
                {
                    SyncError = "ReminderSyncFailed";
                    System.Diagnostics.Debug.WriteLine($"Reminder sync: {ex.GetType().Name}");
                }
            }
        }
        finally { _gate.Release(); }
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }

    private async Task StartRealtimeAsync(string account)
    {
        if (_channelAccount == account) return;
        await _supabase.EnsureRealtimeConnectedAsync();
        _channels.AddRange(await RealtimeSubscriptionHelper.SubscribeTableChanges<ReminderRevision>(
            _supabase.Client, revision => { _ = RefreshSafeAsync(); }, revision => { _ = RefreshSafeAsync(); }, revision => { _ = RefreshSafeAsync(); }));
        _channelAccount = account;
    }

    private async Task PushAsync(string scope, List<TripDTO> trips)
    {
        foreach (var row in (await RowsAsync(scope)).Where(x => x.Dirty))
        {
            if (Scope != scope) return;
            var trip = trips.FirstOrDefault(t => Matches(row, t));
            if (trip is null || trip.RemoteTripId <= 0 || trip.IsInTrash) continue;
            var reminder = Read(row);
            var response = await _supabase.Client.Rpc("save_trip_reminder", new Dictionary<string, object>
            {
                ["p_trip_id"] = trip.RemoteTripId, ["p_operation_id"] = row.OperationId,
                ["p_expected_version"] = row.ExpectedVersion,
                ["p_definition"] = ReadDefinitionPayload(row.DefinitionJson),
                ["p_trip_zone"] = CanonicalZone(trip.TimeZoneId ?? reminder.TimeZoneId)
            });
            if (Scope != scope) return;
            var result = JsonSerializer.Deserialize<SaveResult>(response.Content ?? "{}") ?? throw new JsonException();
            if (result.Status == "ok" && result.Definition is not null)
            {
                row.DefinitionJson = JsonSerializer.Serialize(result.Definition);
                row.ExpectedVersion = result.Definition.Version;
                row.Dirty = false;
                row.SyncError = null;
            }
            else
            {
                row.Dirty = false;
                row.SyncError = result.Status switch
                {
                    "conflict" => "ReminderConflict",
                    "invalid" => "ReminderScheduleError",
                    _ => "ReminderDenied"
                };
                if (result.Definition is not null)
                {
                    row.DefinitionJson = JsonSerializer.Serialize(result.Definition);
                    row.ExpectedVersion = result.Definition.Version;
                }
                else { reminder.Enabled = false; row.DefinitionJson = JsonSerializer.Serialize(reminder); }
            }
            await _db.UpdateAsync(row);
        }
        foreach (var pref in (await PreferencesAsync(scope)).Where(x => x.Dirty))
        {
            if (Scope != scope) return;
            var trip = trips.FirstOrDefault(t => Matches(pref, t));
            if (trip?.RemoteTripId is not > 0) continue;
            var response = await _supabase.Client.Rpc("mute_trip_reminder", new Dictionary<string, object>
            {
                ["p_trip_id"] = trip.RemoteTripId, ["p_reminder_id"] = pref.ReminderId,
                ["p_muted"] = pref.Muted
            });
            if (response.Content?.Trim() != "true") throw new InvalidOperationException("ReminderDenied");
            pref.Dirty = false;
            pref.RemoteTripId = trip.RemoteTripId;
            await _db.UpdateAsync(pref);
        }
    }

    private async Task MergeAsync(string scope, RemoteSnapshot snapshot, List<TripDTO> localTrips)
    {
        var existing = await RowsAsync(scope);
        var pendingTrips = await _db.Table<Packo.Repositories.Models.SQLitePendingTripChange>()
            .Where(x => x.LocalUserId == scope).ToListAsync();
        var returnedIds = snapshot.Entries.Select(x => x.Definition.Id).ToHashSet();
        foreach (var row in existing.Where(x => x.RemoteTripId > 0 && !x.Dirty && !returnedIds.Contains(x.ReminderId)
            && !(Read(x).Version == 0 && x.SyncError is not null)))
            await _db.DeleteAsync(row); // Revocation, deletion, or removal from recipients.
        foreach (var entry in snapshot.Entries)
        {
            var localTrip = localTrips.FirstOrDefault(x => x.RemoteTripId == entry.Trip.RemoteTripId);
            if (localTrip is null) continue; // The normal Trips sync will supply it on the next refresh.
            var previous = existing.FirstOrDefault(x => x.ReminderId == entry.Definition.Id);
            if (previous?.Dirty == true) continue;
            entry.Trip.LocalTripId = localTrip.LocalTripId;
            entry.Trip.LocalUserId = scope;
            entry.Trip.Participants = localTrip.Participants;
            var hasPendingTrip = pendingTrips.Any(x => JsonSerializer.Deserialize<TripDTO>(x.TripJson)?.LocalTripId == localTrip.LocalTripId);
            if (hasPendingTrip)
            {
                entry.Trip.StartDate = localTrip.StartDate;
                entry.Trip.EndDate = localTrip.EndDate;
                entry.Trip.TimeZoneId = localTrip.TimeZoneId;
                entry.Trip.IsInTrash = localTrip.IsInTrash;
            }
            await _db.InsertOrReplaceAsync(new SQLiteTripReminder
            {
                Key = scope + ":" + entry.Definition.Id, LocalUserId = scope,
                ReminderId = entry.Definition.Id, RemoteTripId = entry.Trip.RemoteTripId,
                LocalTripId = localTrip.LocalTripId, TripJson = JsonSerializer.Serialize(entry.Trip),
                DefinitionJson = JsonSerializer.Serialize(entry.Definition), ExpectedVersion = entry.Definition.Version,
                SyncError = previous?.SyncError
            });
            // Persist the canonical trip zone assigned when its first reminder was created.
            if (!hasPendingTrip)
                await _db.ExecuteAsync("UPDATE SQLiteTrip SET StartDate = ?, EndDate = ?, TimeZoneId = ?, IsInTrash = ? WHERE LocalTripId = ? AND LocalUserId = ?",
                    entry.Trip.StartDate, entry.Trip.EndDate, entry.Trip.TimeZoneId, entry.Trip.IsInTrash, localTrip.LocalTripId, scope);
        }
        var preferences = await PreferencesAsync(scope);
        foreach (var pref in preferences.Where(x => x.RemoteTripId > 0 && !x.Dirty)) await _db.DeleteAsync(pref);
        foreach (var pref in snapshot.Preferences)
        {
            var trip = localTrips.FirstOrDefault(x => x.RemoteTripId == pref.TripId);
            if (trip is null || preferences.Any(x => x.Dirty && Matches(x, trip) && x.ReminderId == pref.ReminderId)) continue;
            await _db.InsertOrReplaceAsync(new SQLiteReminderPreference
            {
                Key = PreferenceKey(scope, trip.LocalTripId, trip.RemoteTripId, pref.ReminderId),
                LocalUserId = scope, LocalTripId = trip.LocalTripId, RemoteTripId = trip.RemoteTripId,
                ReminderId = pref.ReminderId, Muted = pref.Muted
            });
        }
    }

    private async Task ScheduleAsync(string scope)
    {
        if (scope != Scope) return;
        var account = UserId;
        var prefs = await PreferencesAsync(scope);
        var scheduled = new List<ScheduledReminder>();
        foreach (var row in await RowsAsync(scope))
        {
            var reminder = Read(row);
            var trip = JsonSerializer.Deserialize<TripDTO>(row.TripJson)!;
            // Include elapsed definitions: Android may still hold their undelivered alarm.
            // The native scheduler retains only a known pending occurrence, never replays a past download.
            if (!reminder.IsRecipient(account) || IsMuted(prefs, row, reminder)
                || !ReminderDelivery.IsActive(reminder, trip, DateTimeOffset.UtcNow)) continue;
            scheduled.Add(new ScheduledReminder { AccountId = scope, Definition = reminder, Trip = trip });
        }
        if (scope != Scope || account != UserId) { await _scheduler.ReplaceAsync("", []); return; }
        await _scheduler.ReplaceAsync(scope, scheduled);
    }

    private static bool IsMuted(List<SQLiteReminderPreference> prefs, SQLiteTripReminder row, TripReminder reminder) =>
        prefs.Any(p => Matches(p, new TripDTO { RemoteTripId = row.RemoteTripId, LocalTripId = row.LocalTripId })
            && p.Muted && (p.ReminderId == reminder.Id || (p.ReminderId == "*" && reminder.Audience != ReminderAudience.Self)));
    private Task<List<SQLiteTripReminder>> RowsAsync(string scope) => _db.Table<SQLiteTripReminder>().Where(x => x.LocalUserId == scope).ToListAsync();
    private Task<List<SQLiteReminderPreference>> PreferencesAsync(string scope) => _db.Table<SQLiteReminderPreference>().Where(x => x.LocalUserId == scope).ToListAsync();
    private static TripReminder Read(SQLiteTripReminder row) => JsonSerializer.Deserialize<TripReminder>(row.DefinitionJson)!;
    internal static Newtonsoft.Json.Linq.JObject ReadDefinitionPayload(string json)
    {
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json))
        { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
        var payload = Newtonsoft.Json.Linq.JObject.Load(reader);
        // Postgrest.Rpc internally parses and serializes the parameters again. ISO
        // timestamps become DateTime tokens in that pass and are converted to UTC.
        // Calendar dates must therefore travel as date-only strings, not midnight timestamps.
        foreach (var field in new[] { "Date", "RepeatUntil" })
        {
            if (payload[field]?.Type == Newtonsoft.Json.Linq.JTokenType.Null) continue;
            var text = payload[field]?.ToObject<string>();
            if (text is null || text.Length < 10 || !DateOnly.TryParseExact(text[..10], "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                throw new JsonException($"Invalid reminder calendar field: {field}");
            payload[field] = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        return payload;
    }
    private static bool Matches(SQLiteTripReminder row, TripDTO trip) => row.RemoteTripId > 0 && trip.RemoteTripId > 0
        ? row.RemoteTripId == trip.RemoteTripId : row.LocalTripId == trip.LocalTripId;
    private static bool Matches(SQLiteReminderPreference row, TripDTO trip) => row.RemoteTripId > 0 && trip.RemoteTripId > 0
        ? row.RemoteTripId == trip.RemoteTripId : row.LocalTripId == trip.LocalTripId;
    private static string PreferenceKey(string scope, int localId, int remoteId, string id) => $"{scope}:{(remoteId > 0 ? "r" + remoteId : "l" + localId)}:{id}";

    private sealed class SaveResult { public string Status { get; set; } = ""; public TripReminder? Definition { get; set; } }
    private sealed class RemoteSnapshot
    {
        public List<RemoteEntry> Entries { get; set; } = [];
        public List<RemotePreference> Preferences { get; set; } = [];
    }
    private sealed class RemoteEntry
    {
        public TripReminder Definition { get; set; } = new();
        public TripDTO Trip { get; set; } = new();
    }
    private sealed class RemotePreference
    {
        public int TripId { get; set; }
        public string ReminderId { get; set; } = "*";
        public bool Muted { get; set; }
    }
}

[global::Supabase.Postgrest.Attributes.Table("TripReminderRevision")]
public sealed class ReminderRevision : global::Supabase.Postgrest.Models.BaseModel
{
    [global::Supabase.Postgrest.Attributes.PrimaryKey("TripId", false)] public int TripId { get; set; }
    [global::Supabase.Postgrest.Attributes.Column("Revision")] public long Revision { get; set; }
}

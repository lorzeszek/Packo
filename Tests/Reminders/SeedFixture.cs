using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using SQLite;
using System.Text.Json;

// Used only with a fresh, isolated emulator. No real account or remote data is involved.
internal static class SeedFixture
{
    public static async Task WriteAsync(string path, bool alarms)
    {
        if (File.Exists(path)) File.Delete(path);
        var db = new SQLiteAsyncConnection(path);
        await db.CreateTableAsync<SQLiteUser>();
        await db.CreateTableAsync<SQLiteTrip>();
        await db.CreateTableAsync<SQLiteTripReminder>();
        await db.CreateTableAsync<SQLiteReminderPreference>();
        const string scope = "reminder-emulator-fixture";
        await db.InsertAsync(new SQLiteUser { LocalUserId = scope, CreatedDate = DateTime.Now });
        var row = new SQLiteTrip { LocalUserId = scope, Destination = "Rzym · test przypomnień",
            StartDate = DateTime.Today.AddDays(-1), EndDate = DateTime.Today.AddDays(5),
            TimeZoneId = alarms ? "Etc/UTC" : "Europe/Warsaw", CreatedDate = DateTime.Now, IsActive = true };
        await db.InsertAsync(row);
        if (alarms)
        {
            // Use a fixed UTC trip zone, independently of the emulator's device zone.
            var upcoming = DateTime.UtcNow.AddMinutes(2);
            var trip = Packo.Repositories.Local.LocalTripRepository.ToTrip(row);
            for (var i = 1; i <= 2; i++)
            {
                var reminder = new TripReminder { Title = "TravelSet test " + i, Body = "Dwa alerty w tym samym czasie",
                    AuthorId = scope, Date = upcoming.Date, MinuteOfDay = upcoming.Hour * 60 + upcoming.Minute,
                    Clock = ReminderClock.Trip, TimeZoneId = "Etc/UTC" };
                await db.InsertAsync(new SQLiteTripReminder { Key = scope + ":" + reminder.Id,
                    LocalUserId = scope, ReminderId = reminder.Id, LocalTripId = row.LocalTripId,
                    DefinitionJson = JsonSerializer.Serialize(reminder), TripJson = JsonSerializer.Serialize(trip),
                    Dirty = true, OperationId = Guid.NewGuid().ToString() });
            }
            Console.WriteLine("Native fixture due at " + upcoming.ToString("HH:mm") + " UTC");
        }
        await db.CloseAsync();
        Console.WriteLine("Wrote isolated emulator fixture: " + path);
    }
}

using Packo.Helpers;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Services.Interfaces;

internal static class DeliveryChecks
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            Console.WriteLine("PASS " + description);
            count++;
        }
        var zone = ReminderSchedule.Zone("Etc/UTC");
        var at = new DateTimeOffset(2027, 7, 10, 18, 0, 0, TimeSpan.Zero);
        var now = at.AddMinutes(45);
        ScheduledReminder Item() => new()
        {
            AccountId = "a", NextAt = at,
            Definition = new TripReminder { Id = "one", AuthorId = "a", Date = at.Date,
                MinuteOfDay = 18 * 60, TimeZoneId = zone.Id, Clock = ReminderClock.Trip },
            Trip = new TripDTO { StartDate = at.Date, EndDate = at.Date.AddDays(3), TimeZoneId = zone.Id }
        };
        var old = Item();
        var current = Item();
        var midnight = new DateTimeOffset(2027, 7, 10, 23, 30, 0, TimeSpan.Zero);
        current.Trip.TimeZoneId = "Europe/Warsaw";
        var ny = ReminderSchedule.Zone("America/New_York");
        Check(ReminderEditorCalendar.Today(current.Definition, current.Trip, midnight, ny) == new DateTime(2027, 7, 11),
            "The editor blocks past dates using the trip calendar for a trip-time reminder");
        current.Definition.Clock = ReminderClock.Device;
        Check(ReminderEditorCalendar.Today(current.Definition, current.Trip, midnight, ny) == new DateTime(2027, 7, 10),
            "The editor uses the device calendar for a device-time reminder across midnight");
        current.Definition.Repeat = ReminderRepeat.Daily;
        Check(ReminderEditorCalendar.PreserveStart(current.Definition, ReminderRepeat.Daily, true, false, at.Date.AddDays(1)),
            "Editing an existing daily series preserves its original past start");
        Check(!ReminderEditorCalendar.PreserveStart(current.Definition, ReminderRepeat.Once, true, false, at.Date.AddDays(1))
            && !ReminderEditorCalendar.PreserveStart(current.Definition, ReminderRepeat.Daily, false, false, at.Date.AddDays(1))
            && !ReminderEditorCalendar.PreserveStart(current.Definition, ReminderRepeat.Daily, true, true, at.Date.AddDays(1)),
            "A new reminder, one-time reminder or explicit start change cannot select a past date");
        current = Item();
        Check(ReminderDelivery.CanDeliver(current, at, now), "An OS-delayed alarm is deliverable after 45 minutes during the trip");
        Check(!ReminderDelivery.CanDeliver(current, at, at.AddSeconds(-1)), "An alarm is never displayed before its scheduled time");
        Check(ReminderDelivery.Next(current, now, zone, [], ReminderDelivery.PendingFrom(old, current)) == at,
            "Refreshing an unchanged overdue one-time alarm preserves its registered occurrence");
        Check(ReminderDelivery.Next(current, now, zone, []) is null,
            "Downloading a past one-time definition does not replay an alarm that was never registered");
        var key = ReminderSchedule.KeyAt(current.Definition, current.Trip, at, zone);
        Check(ReminderDelivery.Next(current, now, zone, [key], at) is null,
            "A delivered one-time occurrence is not replayed on refresh");
        current.Definition.Repeat = old.Definition.Repeat = ReminderRepeat.Daily;
        key = ReminderSchedule.KeyAt(current.Definition, current.Trip, at, zone);
        Check(ReminderDelivery.Next(current, now, zone, [], ReminderDelivery.PendingFrom(old, current)) == at,
            "Refreshing an overdue daily alarm does not skip straight to tomorrow");
        Check(ReminderDelivery.Next(current, now, zone, [key], at) == at.AddDays(1),
            "A delivered daily occurrence schedules tomorrow");
        current.Definition.Title = "Updated title";
        current.Definition.Version++;
        Check(ReminderDelivery.PendingFrom(old, current) == at,
            "A server version or text change preserves the pending occurrence");
        current.Definition.MinuteOfDay++;
        Check(ReminderDelivery.PendingFrom(old, current) is null, "Editing an alarm time invalidates the previous pending occurrence");
        old = Item(); current = Item(); current.Trip.EndDate = at.AddMinutes(10).DateTime;
        Check(ReminderDelivery.PendingFrom(old, current) is null && !ReminderDelivery.CanDeliver(current, at, now),
            "Shortening the trip cancels pending delivery after its new end");
        current = Item(); current.AccountId = "b";
        Check(ReminderDelivery.PendingFrom(old, current) is null, "A different account cannot inherit a pending alarm");
        current = Item(); current.Trip.IsInTrash = true;
        Check(!ReminderDelivery.CanDeliver(current, at, now), "Trashed trips cannot deliver pending alarms");
        current = Item(); current.Definition.Enabled = false;
        Check(!ReminderDelivery.CanDeliver(current, at, now), "Disabled reminders cannot deliver pending alarms");
        current = Item(); current.Definition.Deleted = true;
        Check(!ReminderDelivery.CanDeliver(current, at, now), "Deleted reminders cannot deliver pending alarms");
        Console.WriteLine($"{count} reminder delivery checks passed.");
    }
}

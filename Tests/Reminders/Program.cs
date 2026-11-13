using Packo.Helpers;
using Packo.Models;
using Packo.Models.DTO;

if (args.Length >= 2 && args[0] == "--seed-emulator")
{
    await SeedFixture.WriteAsync(args[1], args.Length >= 3 && args[2] == "alarms", args.Length >= 3 && args[2] == "navigation",
        args.Length >= 3 && args[2] == "ui");
    return;
}
if (args.Length == 1 && args[0] == "--navigation-only")
{
    await NavigationChecks.RunAsync();
    return;
}
if (args.Length == 1 && args[0] == "--delivery-only")
{
    DeliveryChecks.Run();
    return;
}

var count = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description);
    count++;
}
var warsaw = ReminderSchedule.Zone("Europe/Warsaw");
var tokyo = ReminderSchedule.Zone("Asia/Tokyo");
var ny = ReminderSchedule.Zone("America/New_York");
DateTimeOffset Utc(int day, int hour = 0, int month = 7) => new(2027, month, day, hour, 0, 0, TimeSpan.Zero);
var trip = new TripDTO { StartDate = new DateTime(2027, 7, 10, 12, 0, 0),
    EndDate = new DateTime(2027, 7, 20, 20, 0, 0), TimeZoneId = "Europe/Warsaw" };
TripReminder Daily() => new() { Title = "Dinner", Body = "Remember", Date = new DateTime(2027, 7, 10),
    MinuteOfDay = 18 * 60, Repeat = ReminderRepeat.Daily, Clock = ReminderClock.Device,
    TimeZoneId = "Europe/Warsaw", AuthorId = "a" };
var reminder = Daily();
var novemberTrip = new TripDTO { StartDate = new DateTime(2026, 11, 14), EndDate = new DateTime(2026, 11, 20), TimeZoneId = "Europe/Warsaw" };
var novemberReminder = new TripReminder { Title = "Documents", Body = "Passport", Date = new DateTime(2026, 11, 14),
    Clock = ReminderClock.Trip, MinuteOfDay = 18 * 60, TimeZoneId = "Europe/Warsaw" };
var novemberNow = new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.Zero);
novemberReminder.Date = new DateTime(2026, 11, 13);
Check(ReminderSchedule.Validate(novemberReminder, novemberTrip, novemberNow, warsaw) is null
    && ReminderSchedule.Next(novemberReminder, novemberTrip, novemberNow, warsaw)?.Day == 13,
    "Changing November 14 to November 13 is valid before departure when the chosen hour is still ahead");
novemberReminder.Repeat = ReminderRepeat.Daily;
novemberReminder.MinuteOfDay = 10 * 60;
Check(ReminderSchedule.Validate(novemberReminder, novemberTrip, novemberNow, warsaw) is null
    && novemberReminder.Date.Day == 13 && ReminderSchedule.Next(novemberReminder, novemberTrip, novemberNow, warsaw)?.Day == 14,
    "A daily series may start on November 13 while its next alert is November 14 after today's hour has passed");
Check(ReminderSchedule.Next(reminder, trip, Utc(10), warsaw) == Utc(10, 16), "18:00 follows the device's Warsaw zone");
Check(ReminderSchedule.Next(reminder, trip, Utc(10), tokyo) == Utc(10, 9), "18:00 follows the new Tokyo device zone offline");
Check(ReminderSchedule.Next(reminder, trip, Utc(10), ny) == Utc(10, 22), "18:00 follows a westward zone change");
reminder.Clock = ReminderClock.Trip;
Check(ReminderSchedule.Next(reminder, trip, Utc(10), tokyo) == Utc(10, 16), "A fixed trip-zone reminder is the same instant for all recipients");
reminder.Clock = ReminderClock.Device;
Check(ReminderSchedule.Next(reminder, trip, Utc(10, 17), warsaw) == Utc(11, 16), "A daily series skips a passed occurrence");
Check(ReminderSchedule.Next(reminder, trip, Utc(10), warsaw, "2027-07-10") == Utc(11, 16), "An already delivered calendar occurrence is skipped after a zone change");
reminder.Repeat = ReminderRepeat.Weekly;
Check(ReminderSchedule.Next(reminder, trip, Utc(11), warsaw) == Utc(17, 16), "Weekly reminders preserve their calendar weekday");
reminder.RepeatUntil = new DateTime(2027, 7, 16);
Check(ReminderSchedule.Next(reminder, trip, Utc(11), warsaw) is null, "A custom series end caps recurrence");
reminder = Daily();
var shorter = new TripDTO { StartDate = trip.StartDate, EndDate = new DateTime(2027, 7, 11, 12, 0, 0), TimeZoneId = trip.TimeZoneId };
Check(ReminderSchedule.Next(reminder, shorter, Utc(11), warsaw) is null, "Shortening a trip excludes later same-day reminders");
Check(ReminderSchedule.Next(reminder, trip, Utc(21), warsaw) is null, "An ended trip cannot schedule alerts");
trip.IsInTrash = true;
Check(ReminderSchedule.Next(reminder, trip, Utc(10), warsaw) is null, "Trashing a trip stops its alarms");
trip.IsInTrash = false;
reminder.Enabled = false;
Check(ReminderSchedule.Next(reminder, trip, Utc(10), warsaw) is null, "Disabled reminders cannot schedule alarms");
reminder.Enabled = true; reminder.Deleted = true;
Check(ReminderSchedule.Next(reminder, trip, Utc(10), warsaw) is null, "Deleted reminders cannot schedule alarms");
reminder = Daily(); reminder.Timing = ReminderTiming.HoursBefore; reminder.Repeat = ReminderRepeat.Once; reminder.Offset = 3;
Check(ReminderSchedule.Next(reminder, trip, Utc(9), tokyo) == Utc(10, 7), "Hours-before is anchored to the trip instant");
trip.StartDate = trip.StartDate.Value.AddDays(1);
Check(ReminderSchedule.Next(reminder, trip, Utc(9), warsaw) == Utc(11, 7), "Changing departure moves relative reminders");
reminder.Timing = ReminderTiming.DaysBefore; reminder.Offset = 2;
Check(ReminderSchedule.Next(reminder, trip, Utc(8), warsaw) == Utc(9, 16), "Days-before uses a chosen calendar time, unlike elapsed hours");
var springTrip = new TripDTO { StartDate = new DateTime(2027, 3, 28, 4, 0, 0), EndDate = new DateTime(2027, 4, 1), TimeZoneId = "Europe/Warsaw" };
reminder.Timing = ReminderTiming.HoursBefore; reminder.Offset = 3;
Check(ReminderSchedule.Next(reminder, springTrip, Utc(27, month: 3), warsaw) == Utc(27, 23, 3), "Hours-before measures elapsed hours across spring DST");
var fallTrip = new TripDTO { StartDate = new DateTime(2027, 10, 31, 4, 0, 0), EndDate = new DateTime(2027, 11, 2), TimeZoneId = "Europe/Warsaw" };
reminder.Offset = 2;
Check(ReminderSchedule.Next(reminder, fallTrip, Utc(30, month: 10), warsaw) == Utc(31, 1, 10), "Hours-before preserves the correct occurrence in a fall overlap");
Check(ReminderSchedule.Instant(new DateTime(2027, 3, 28, 2, 30, 0), warsaw) == Utc(28, 1, 3), "A nonexistent local time advances to the first valid minute");
Check(ReminderSchedule.Instant(new DateTime(2027, 10, 31, 2, 30, 0), warsaw) == new DateTimeOffset(2027, 10, 31, 0, 30, 0, TimeSpan.Zero), "A repeated local time selects its first occurrence");
reminder = Daily(); reminder.Date = new DateTime(2027, 3, 27);
Check(ReminderSchedule.Next(reminder, springTrip, Utc(27, 18, 3), warsaw) == Utc(28, 16, 3), "Daily means local calendar time across DST, not plus 24 hours");
reminder = Daily(); reminder.Repeat = ReminderRepeat.Once;
Check(ReminderSchedule.Validate(reminder, trip, Utc(11), warsaw) == "ReminderPast", "A past one-time reminder is rejected");
reminder.Date = new DateTime(2027, 7, 21);
Check(ReminderSchedule.Validate(reminder, trip, Utc(9), warsaw) == "ReminderOutsideTrip", "An absolute reminder after trip end is rejected");
reminder = Daily(); reminder.Title = " ";
Check(ReminderSchedule.Validate(reminder, trip, Utc(9), warsaw) == "ReminderTextError", "Blank titles are rejected");
reminder = Daily(); reminder.Audience = ReminderAudience.Selected;
Check(ReminderSchedule.Validate(reminder, trip, Utc(9), warsaw) == "ReminderSelectPeople", "Selected recipients cannot be empty");
reminder.RecipientIds = ["b"];
Check(!reminder.IsRecipient("a") && reminder.IsRecipient("b"), "Selecting another person does not implicitly notify the author");
var other = Daily(); other.AuthorId = "b";
Check(ReminderSchedule.Collides(reminder, other, trip, Utc(9), warsaw), "Overlapping recipients get an advisory collision warning");
reminder.Audience = ReminderAudience.Self;
Check(!ReminderSchedule.Collides(reminder, other, trip, Utc(9), warsaw), "Unrelated personal reminders do not collide");
other.Audience = ReminderAudience.All;
Check(ReminderSchedule.Collides(reminder, other, trip, Utc(9), warsaw), "Group reminders collide with personal reminders at the same instant");
other.MinuteOfDay += 1;
Check(!ReminderSchedule.Collides(reminder, other, trip, Utc(9), warsaw), "Different times do not collide");
Console.WriteLine($"{count} reminder schedule checks passed.");
DeliveryChecks.Run();
await ServiceChecks.RunAsync();
await TransportChecks.RunAsync();
await DeviceIntegrationChecks.RunAsync();
await NavigationChecks.RunAsync();

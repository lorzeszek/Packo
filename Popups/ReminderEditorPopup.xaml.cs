using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Helpers;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Services;
using System.Globalization;

namespace Packo.Popups;

public sealed record ReminderEditorResult(IReadOnlyList<ReminderEntry> Entries, bool TripMuted);

public partial class ReminderEditorPopup : Popup<ReminderEditorResult?>
{
    private readonly TripReminderService _service;
    private readonly TripDTO _trip;
    private readonly TripReminder _initial;
    private readonly IReadOnlyList<ReminderEntry> _otherReminders;
    private readonly List<(string Id, CheckBox Check)> _people = [];
    private string? _confirmedCollision;
    private bool _saving;
    private readonly bool _existing;
    private bool _changingStart;
    private bool _preserveStart;
    private ReminderTiming _timing;
    private ReminderClock _clock;
    private ReminderRepeat _repeat;
    private ReminderAudience _audience;
    private static LocalizationResourceManager S => LocalizationResourceManager.Instance;

    private ReminderEditorPopup(Page page, TripReminderService service, TripDTO trip, TripReminder reminder,
        IReadOnlyList<ReminderEntry> otherReminders)
    {
        InitializeComponent();
        (_service, _trip, _initial, _otherReminders) = (service, trip, reminder, otherReminders);
        _existing = otherReminders.Any(x => x.Definition.Id == reminder.Id);
        (_timing, _clock, _repeat, _audience) = (reminder.Timing, reminder.Clock, reminder.Repeat, reminder.Audience);
        PopupCard.WidthRequest = PopupCardSizing.GetWidth(page);
        var availableHeight = page.Height > 0 ? page.Height : 740;
        PopupCard.MaximumHeightRequest = Math.Max(240, availableHeight - 70);
        FormScroll.MaximumHeightRequest = Math.Clamp(availableHeight - 270, 140, 470);
        Heading.Text = S[reminder.Version == 0 && !otherReminders.Any(x => x.Definition.Id == reminder.Id) ? "ReminderNewHeading" : "ReminderEditHeading"];
        TitleEntry.Text = reminder.Title;
        BodyEditor.Text = reminder.Body;
        FirstDatePicker.Date = reminder.Date.Date;
        ClockPicker.Time = TimeSpan.FromMinutes(reminder.MinuteOfDay);
        OffsetEntry.Text = reminder.Offset.ToString(CultureInfo.InvariantCulture);
        RepeatEndPicker.Date = reminder.RepeatUntil?.Date ?? trip.EndDate?.Date ?? DateTime.Today;
        CustomEndSwitch.IsToggled = reminder.RepeatUntil.HasValue;
        AudienceChoice.IsEnabled = trip.RemoteTripId > 0 && trip.Participants.Count > 0;
        foreach (var participant in trip.Participants)
        {
            var check = new CheckBox { IsChecked = reminder.RecipientIds.Contains(participant.UserId), VerticalOptions = LayoutOptions.Center };
            _people.Add((participant.UserId, check));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)), ColumnSpacing = 6 };
            row.Add(check);
            row.Add(new Label { Text = string.IsNullOrWhiteSpace(participant.Name) ? S["ReminderParticipant"] : participant.Name,
                VerticalOptions = LayoutOptions.Center, FontSize = 14, LineBreakMode = LineBreakMode.WordWrap }, 1);
            PeopleList.Children.Add(row);
        }
        UpdateFields();
    }

    public static async Task<ReminderEditorResult?> ShowAsync(Page page, TripReminderService service, TripDTO trip,
        TripReminder reminder, IReadOnlyList<ReminderEntry> reminders)
    {
        var popup = new ReminderEditorPopup(page, service, trip, reminder.Copy(), reminders);
        return (await page.ShowPopupAsync<ReminderEditorResult>(popup)).Result;
    }

    private void Choose(string heading, string[] keys, int selected, Action<int> apply)
    {
        if (_saving) return;
        TitleEntry.Unfocus(); BodyEditor.Unfocus();
        ChoiceHeading.Text = S[heading];
        ChoiceOptions.Children.Clear();
        for (var i = 0; i < keys.Length; i++)
        {
            var index = i;
            var button = new Button { Text = (i == selected ? "✓  " : "") + S[keys[i]],
                Style = (Style)Application.Current!.Resources["ReminderChoiceButtonStyle"] };
            if (i == selected)
                button.SetAppThemeColor(Button.BackgroundColorProperty,
                    (Color)Application.Current.Resources["PackoAccentSoftLight"], (Color)Application.Current.Resources["PackoAccentSoftDark"]);
            button.Clicked += (_, _) => { apply(index); UpdateFields(); CloseChoice(); };
            ChoiceOptions.Children.Add(button);
        }
        FormPanel.IsVisible = false;
        ChoicePanel.IsVisible = true;
    }
    private void CloseChoice() { ChoicePanel.IsVisible = false; FormPanel.IsVisible = true; }
    private void OnChoiceCancelClicked(object? sender, EventArgs e) => CloseChoice();
    private void OnTimingChoiceClicked(object? sender, EventArgs e) => Choose("ReminderWhen",
        ["ReminderAtDate", "ReminderHoursBefore", "ReminderDaysBefore"], (int)_timing, i => _timing = (ReminderTiming)i);
    private void OnZoneChoiceClicked(object? sender, EventArgs e) => Choose("ReminderClock",
        ["ReminderDeviceClock", "ReminderTripClock"], (int)_clock, i => _clock = (ReminderClock)i);
    private void OnRepeatChoiceClicked(object? sender, EventArgs e) => Choose("ReminderRepeat",
        ["ReminderOnce", "ReminderDaily", "ReminderWeekly"], (int)_repeat, i =>
        {
            _repeat = (ReminderRepeat)i;
            if (_timing == ReminderTiming.Date) _clock = _repeat == ReminderRepeat.Once ? ReminderClock.Trip : ReminderClock.Device;
        });
    private void OnAudienceChoiceClicked(object? sender, EventArgs e) => Choose("ReminderRecipients",
        ["ReminderOnlyMe", "ReminderEveryone", "ReminderSelected"], (int)_audience, i => _audience = (ReminderAudience)i);
    private void OnRepeatEndChanged(object? sender, ToggledEventArgs e) => UpdateFields();
    private void OnFirstDateChanged(object? sender, DateChangedEventArgs e) { if (_trip is not null) UpdateEndMinimum(); }
    private void OnChangeStartClicked(object? sender, EventArgs e) { _changingStart = true; UpdateFields(); }

    private void UpdateFields()
    {
        if (_trip is null) return; // Picker events can run during InitializeComponent.
        DateFields.IsVisible = _timing == ReminderTiming.Date;
        OffsetFields.IsVisible = _timing != ReminderTiming.Date;
        OffsetCaption.Text = S[_timing == ReminderTiming.HoursBefore ? "ReminderNumberHours" : "ReminderNumberDays"];
        TimeFields.IsVisible = _timing != ReminderTiming.HoursBefore;
        ZoneFields.IsVisible = _timing == ReminderTiming.Date;
        TimingChoice.Text = S[_timing switch { ReminderTiming.HoursBefore => "ReminderHoursBefore", ReminderTiming.DaysBefore => "ReminderDaysBefore", _ => "ReminderAtDate" }] + "  ⌄";
        ZoneChoice.Text = S[_clock == ReminderClock.Device ? "ReminderDeviceClock" : "ReminderTripClock"] + "  ⌄";
        RepeatChoice.Text = S[_repeat switch { ReminderRepeat.Daily => "ReminderDaily", ReminderRepeat.Weekly => "ReminderWeekly", _ => "ReminderOnce" }] + "  ⌄";
        AudienceChoice.Text = S[_audience switch { ReminderAudience.All => "ReminderEveryone", ReminderAudience.Selected => "ReminderSelected", _ => "ReminderOnlyMe" }] + "  ⌄";
        ZoneHint.Text = S["ReminderTripZone"] + ": " + (_trip.TimeZoneId ?? _initial.TimeZoneId);
        RepeatEndFields.IsVisible = _repeat != ReminderRepeat.Once;
        RepeatEndPicker.IsVisible = CustomEndSwitch.IsToggled;
        PeopleList.IsVisible = _audience == ReminderAudience.Selected;
        var dateSettings = _initial.Copy(); dateSettings.Clock = _clock; dateSettings.Timing = _timing;
        var today = ReminderEditorCalendar.Today(dateSettings, _trip, DateTimeOffset.UtcNow, TripSchedule.DeviceTimeZone);
        _preserveStart = ReminderEditorCalendar.PreserveStart(_initial, _repeat, _existing, _changingStart, today);
        OriginalStartPanel.IsVisible = _preserveStart;
        OriginalStartLabel.Text = _initial.Date.ToString("dd MMM yyyy");
        FirstDatePicker.IsVisible = !_preserveStart;
        FirstDatePicker.MinimumDate = today;
        if (FirstDatePicker.Date is null || FirstDatePicker.Date < today) FirstDatePicker.Date = today;
        UpdateEndMinimum();
    }

    private void UpdateEndMinimum()
    {
        var minimum = FirstDatePicker.MinimumDate;
        if (!_preserveStart && FirstDatePicker.Date is { } first && first > minimum) minimum = first;
        RepeatEndPicker.MinimumDate = minimum;
        if (RepeatEndPicker.Date is null || RepeatEndPicker.Date < minimum) RepeatEndPicker.Date = minimum;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        ErrorLabel.IsVisible = false;
        TitleEntry.Unfocus();
        BodyEditor.Unfocus();
        try
        {
            var value = _initial.Copy();
            value.Title = TitleEntry.Text?.Trim() ?? "";
            value.Body = BodyEditor.Text?.Trim() ?? "";
            value.Timing = _timing;
            value.Date = DateTime.SpecifyKind((_preserveStart ? _initial.Date : FirstDatePicker.Date ?? DateTime.Today).Date, DateTimeKind.Unspecified);
            value.MinuteOfDay = (int)(ClockPicker.Time ?? TimeSpan.Zero).TotalMinutes;
            value.Repeat = _repeat;
            value.RepeatUntil = value.Repeat != ReminderRepeat.Once && CustomEndSwitch.IsToggled && RepeatEndPicker.Date is { } until
                ? DateTime.SpecifyKind(until.Date, DateTimeKind.Unspecified) : null;
            value.Clock = _clock;
            value.TimeZoneId = TripReminderService.CanonicalZone(value.Clock == ReminderClock.Device && value.Timing == ReminderTiming.Date
                ? Packo.Helpers.TripSchedule.DeviceTimeZone.Id : _trip.TimeZoneId ?? value.TimeZoneId);
            value.Audience = _audience;
            value.RecipientIds = _people.Where(x => x.Check.IsChecked).Select(x => x.Id).ToList();
            value.Enabled = true;
            if (!int.TryParse(OffsetEntry.Text, out var offset) || offset is < 0 or > 8760)
            { ShowError("ReminderScheduleError"); return; }
            value.Offset = offset;
            var validation = ReminderSchedule.Validate(value, _trip, DateTimeOffset.UtcNow, Packo.Helpers.TripSchedule.DeviceTimeZone);
            if (validation is not null) { ShowError(validation); return; }
            var fingerprint = System.Text.Json.JsonSerializer.Serialize(value);
            if (_confirmedCollision != fingerprint && _otherReminders.Any(other =>
                ReminderSchedule.Collides(value, other.Definition, _trip, DateTimeOffset.UtcNow, Packo.Helpers.TripSchedule.DeviceTimeZone)))
            {
                _confirmedCollision = fingerprint;
                ShowError("ReminderCollision");
                SaveButton.Text = S["ReminderSaveAnyway"];
                return;
            }
            if (_service.Scheduler.IsSupported && !_service.Scheduler.HasPermission
                && !await _service.Scheduler.RequestPermissionAsync())
            {
                // The definition can still be saved and shared. Permission status remains visible in the list.
                ErrorLabel.Text = S["ReminderPermissionMissing"];
            }
            SetSaving(true);
            await _service.SaveAsync(_trip, value);
            // Keep progress visible until the next popup has all of its data.
            var entries = await _service.GetAsync(_trip);
            var tripMuted = await _service.IsTripMutedAsync(_trip);
            await CloseAsync(new ReminderEditorResult(entries, tripMuted));
        }
        catch (Exception ex)
        {
            ShowError(ex is InvalidOperationException && S[ex.Message] != ex.Message ? ex.Message : "ReminderSaveFailed");
        }
        finally { _saving = false; SetSaving(false); SaveButton.IsEnabled = true; }
    }

    private void SetSaving(bool saving)
    {
        SavingPanel.IsVisible = saving;
        SavingIndicator.IsRunning = saving;
        FormScroll.IsEnabled = !saving;
        CancelButton.IsEnabled = !saving;
        SaveButton.Text = S[saving ? "ReminderSaving" : _confirmedCollision is not null ? "ReminderSaveAnyway" : "SaveChanges"];
    }

    private void ShowError(string key)
    {
        ErrorLabel.Text = S[key];
        ErrorLabel.IsVisible = true;
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (!_saving) { _saving = true; await CloseAsync(null); }
    }
}

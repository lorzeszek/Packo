using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Helpers;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Services;
using System.Globalization;

namespace Packo.Popups;

public partial class ReminderEditorPopup : Popup<bool>
{
    private readonly TripReminderService _service;
    private readonly TripDTO _trip;
    private readonly TripReminder _initial;
    private readonly IReadOnlyList<ReminderEntry> _otherReminders;
    private readonly List<(string Id, CheckBox Check)> _people = [];
    private string? _confirmedCollision;
    private bool _saving;
    private bool _initializing = true;
    private static LocalizationResourceManager S => LocalizationResourceManager.Instance;

    private ReminderEditorPopup(Page page, TripReminderService service, TripDTO trip, TripReminder reminder,
        IReadOnlyList<ReminderEntry> otherReminders)
    {
        InitializeComponent();
        (_service, _trip, _initial, _otherReminders) = (service, trip, reminder, otherReminders);
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
        TimingPicker.ItemsSource = new[] { S["ReminderAtDate"], S["ReminderHoursBefore"], S["ReminderDaysBefore"] };
        TimingPicker.SelectedIndex = (int)reminder.Timing;
        ZonePicker.ItemsSource = new[] { S["ReminderDeviceClock"], S["ReminderTripClock"] };
        ZonePicker.SelectedIndex = (int)reminder.Clock;
        RepeatPicker.ItemsSource = new[] { S["ReminderOnce"], S["ReminderDaily"], S["ReminderWeekly"] };
        RepeatPicker.SelectedIndex = (int)reminder.Repeat;
        RepeatEndPicker.Date = reminder.RepeatUntil?.Date ?? trip.EndDate?.Date ?? DateTime.Today;
        CustomEndSwitch.IsToggled = reminder.RepeatUntil.HasValue;
        AudiencePicker.ItemsSource = new[] { S["ReminderOnlyMe"], S["ReminderEveryone"], S["ReminderSelected"] };
        AudiencePicker.SelectedIndex = (int)reminder.Audience;
        AudiencePicker.IsEnabled = trip.RemoteTripId > 0 && trip.Participants.Count > 0;
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
        _initializing = false;
    }

    public static async Task<bool> ShowAsync(Page page, TripReminderService service, TripDTO trip,
        TripReminder reminder, IReadOnlyList<ReminderEntry> reminders)
    {
        var popup = new ReminderEditorPopup(page, service, trip, reminder.Copy(), reminders);
        return (await page.ShowPopupAsync<bool>(popup)).Result;
    }

    private void OnTimingChanged(object? sender, EventArgs e) => UpdateFields();
    private void OnRepeatChanged(object? sender, EventArgs e)
    {
        if (!_initializing && TimingPicker.SelectedIndex == 0)
            ZonePicker.SelectedIndex = RepeatPicker.SelectedIndex == 0 ? 1 : 0;
        UpdateFields();
    }
    private void OnRepeatEndChanged(object? sender, ToggledEventArgs e) => UpdateFields();
    private void OnAudienceChanged(object? sender, EventArgs e) => UpdateFields();

    private void UpdateFields()
    {
        if (_trip is null) return; // Picker events can run during InitializeComponent.
        DateFields.IsVisible = TimingPicker.SelectedIndex == 0;
        OffsetFields.IsVisible = TimingPicker.SelectedIndex > 0;
        OffsetCaption.Text = S[TimingPicker.SelectedIndex == 1 ? "ReminderNumberHours" : "ReminderNumberDays"];
        TimeFields.IsVisible = TimingPicker.SelectedIndex != 1;
        ZoneFields.IsVisible = TimingPicker.SelectedIndex == 0;
        ZoneHint.Text = S["ReminderTripZone"] + ": " + (_trip.TimeZoneId ?? _initial.TimeZoneId);
        RepeatEndFields.IsVisible = RepeatPicker.SelectedIndex > 0;
        RepeatEndPicker.IsVisible = CustomEndSwitch.IsToggled;
        PeopleList.IsVisible = AudiencePicker.SelectedIndex == 2;
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
            value.Timing = (ReminderTiming)TimingPicker.SelectedIndex;
            value.Date = DateTime.SpecifyKind((FirstDatePicker.Date ?? DateTime.Today).Date, DateTimeKind.Unspecified);
            value.MinuteOfDay = (int)(ClockPicker.Time ?? TimeSpan.Zero).TotalMinutes;
            value.Repeat = (ReminderRepeat)RepeatPicker.SelectedIndex;
            value.RepeatUntil = value.Repeat != ReminderRepeat.Once && CustomEndSwitch.IsToggled && RepeatEndPicker.Date is { } until
                ? DateTime.SpecifyKind(until.Date, DateTimeKind.Unspecified) : null;
            value.Clock = (ReminderClock)ZonePicker.SelectedIndex;
            value.TimeZoneId = TripReminderService.CanonicalZone(value.Clock == ReminderClock.Device && value.Timing == ReminderTiming.Date
                ? Packo.Helpers.TripSchedule.DeviceTimeZone.Id : _trip.TimeZoneId ?? value.TimeZoneId);
            value.Audience = (ReminderAudience)AudiencePicker.SelectedIndex;
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
            await _service.SaveAsync(_trip, value);
            await CloseAsync(true);
        }
        catch (Exception ex)
        {
            ShowError(ex is InvalidOperationException && S[ex.Message] != ex.Message ? ex.Message : "ReminderSaveFailed");
        }
        finally { _saving = false; SaveButton.IsEnabled = true; }
    }

    private void ShowError(string key)
    {
        ErrorLabel.Text = S[key];
        ErrorLabel.IsVisible = true;
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        if (!_saving) { _saving = true; await CloseAsync(false); }
    }
}

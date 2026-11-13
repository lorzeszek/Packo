using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Helpers;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Services;

namespace Packo.Popups;

public sealed record ReminderAction(string Kind, string? Id = null);
public sealed record ReminderListItem(ReminderEntry Entry, bool CanEdit, string Details, string State, string When, string Summary)
{
    private static LocalizationResourceManager S => LocalizationResourceManager.Instance;
    public string Title => Entry.Definition.Title;
    public string Body => Entry.Definition.Body;
    public string ShortState => State.Split('\n')[0];
    public string ToggleText => S[Entry.Definition.Enabled ? "ReminderDisable" : "ReminderEnable"];
    public bool CanMute => !Entry.GroupMuted;
    public string MuteText => S[Entry.IndividuallyMuted ? "ReminderUnmute" : "ReminderMute"];
}

public partial class ReminderListPopup : Popup<ReminderAction>
{
    private static readonly SemaphoreSlim FlowGate = new(1, 1);
    private readonly TripReminderService _service;
    private readonly bool _tripMuted;
    private bool _closing;
    private ReminderListItem? _selected;
    private static LocalizationResourceManager S => LocalizationResourceManager.Instance;

    private ReminderListPopup(Page page, TripReminderService service, TripDTO trip,
        IReadOnlyList<ReminderEntry> entries, bool tripMuted)
    {
        InitializeComponent();
        (_service, _tripMuted) = (service, tripMuted);
        PopupCard.WidthRequest = PopupCardSizing.GetWidth(page);
        PopupCard.MaximumHeightRequest = Math.Max(240, (page.Height > 0 ? page.Height : 740) - 70);
        ListScroll.MaximumHeightRequest = Math.Clamp((page.Height > 0 ? page.Height : 740) - 270, 140, 470);
        TripLabel.Text = trip.Destination;
        MuteTripButton.Text = S[tripMuted ? "ReminderUnmuteTrip" : "ReminderMuteTrip"];
        MuteTripButton.IsVisible = trip.Participants.Count > 0 || trip.IsJoinedTrip;
        EmptyLabel.IsVisible = entries.Count == 0;
        AddButton.IsEnabled = trip.EndDate.HasValue && ReminderSchedule.Instant(trip.EndDate.Value,
            ReminderSchedule.Zone(trip.TimeZoneId)) > DateTimeOffset.UtcNow && !trip.IsInTrash;
        PermissionButton.IsVisible = service.Scheduler.IsSupported && !service.Scheduler.HasPermission;
        TimingButton.IsVisible = service.Scheduler.IsSupported && !service.Scheduler.HasExactTiming;
        StatusLabel.Text = !service.Scheduler.IsSupported ? S["ReminderDesktopHint"]
            : !service.Scheduler.HasPermission ? S["ReminderPermissionMissing"]
            : !service.Scheduler.HasExactTiming ? S["ReminderApproximateHint"] : S["ReminderOfflineHint"];
        if (service.SyncError is not null) StatusLabel.Text += "\n" + S[service.SyncError];
        BindingContext = entries.Select(entry => Item(service, trip, entry)).ToList();
    }

    public static ReminderListItem Item(TripReminderService service, TripDTO trip, ReminderEntry entry)
    {
        var reminder = entry.Definition;
        var next = ReminderSchedule.Next(reminder, trip, DateTimeOffset.UtcNow, Packo.Helpers.TripSchedule.DeviceTimeZone);
        var recurrence = S[reminder.Repeat switch { ReminderRepeat.Daily => "ReminderDaily", ReminderRepeat.Weekly => "ReminderWeekly", _ => "ReminderOnce" }];
        var people = reminder.Audience switch
        {
            ReminderAudience.All => S["ReminderEveryone"],
            ReminderAudience.Selected => string.Join(", ", trip.Participants.Where(p => reminder.RecipientIds.Contains(p.UserId)).Select(p => p.Name)),
            _ => S["ReminderOnlyMe"]
        };
        var zoneText = reminder.Clock == ReminderClock.Device && reminder.Timing == ReminderTiming.Date
            ? S["ReminderDeviceClock"] : S["ReminderTripClock"] + ": " + (trip.TimeZoneId ?? reminder.TimeZoneId);
        var details = (next.HasValue ? S["ReminderNextOccurrence"] + ": "
            + TimeZoneInfo.ConvertTime(next.Value, Packo.Helpers.TripSchedule.DeviceTimeZone).ToString("dd MMM yyyy, HH:mm") : S["ReminderNoNext"])
            + "\n" + recurrence + " · " + people + "\n" + zoneText;
        if (reminder.Repeat != ReminderRepeat.Once && reminder.Timing == ReminderTiming.Date)
            details += "\n" + S["ReminderSeriesStart"] + ": " + reminder.Date.ToString("dd MMM yyyy");
        if (!string.IsNullOrWhiteSpace(reminder.AuthorName)) details += "\n" + S["ReminderAuthor"] + ": " + reminder.AuthorName;
        var state = !reminder.IsRecipient(service.UserId) ? S["ReminderNotRecipient"]
            : !reminder.Enabled ? S["ReminderDisabled"] : !next.HasValue ? S["ReminderNeedsReview"]
            : entry.Muted ? S["ReminderMuted"] : S["ReminderActive"];
        if (entry.Pending) state += " · " + S["ReminderPending"];
        if (entry.GroupMuted) state += "\n" + S["ReminderMutedByTrip"];
        if (entry.Error is not null) state += "\n" + S[entry.Error];
        var when = next.HasValue
            ? TimeZoneInfo.ConvertTime(next.Value, TripSchedule.DeviceTimeZone).ToString("dd MMM yyyy · HH:mm")
            : S["ReminderNoNext"];
        return new(entry, service.CanEdit(reminder, trip), details, state, when, recurrence + " · " + people);
    }

    public static async Task ShowAsync(Page page, TripReminderService service, TripDTO trip)
    {
        if (!await FlowGate.WaitAsync(0)) return;
        try
        {
            await service.RefreshSafeAsync();
            var first = true;
            ReminderEditorResult? snapshot = null;
            while (true)
            {
                var entries = snapshot?.Entries ?? await service.GetAsync(trip);
                if (first && entries.Count == 0 && !trip.IsInTrash && trip.EndDate.HasValue
                    && ReminderSchedule.Instant(trip.EndDate.Value, ReminderSchedule.Zone(trip.TimeZoneId)) > DateTimeOffset.UtcNow)
                {
                    first = false;
                    snapshot = await ReminderEditorPopup.ShowAsync(page, service, trip, service.New(trip), entries);
                    if (snapshot is null) return;
                    continue;
                }
                first = false;
                var muted = snapshot?.TripMuted ?? await service.IsTripMutedAsync(trip);
                snapshot = null;
                var result = (await page.ShowPopupAsync<ReminderAction>(new ReminderListPopup(page, service, trip, entries, muted))).Result;
                if (result is null || result.Kind == "close") return;
                if (result.Kind == "add")
                    snapshot = await ReminderEditorPopup.ShowAsync(page, service, trip, service.New(trip), entries);
                else if (result.Kind == "tripmute") await service.SetMutedAsync(trip, "*", !muted);
                else if (entries.FirstOrDefault(x => x.Definition.Id == result.Id) is { } entry)
                {
                    if (result.Kind == "edit") snapshot = await ReminderEditorPopup.ShowAsync(page, service, trip, entry.Definition, entries);
                    else if (result.Kind == "mute") await service.SetMutedAsync(trip, entry.Definition.Id, !entry.IndividuallyMuted);
                    else if (result.Kind == "delete" && service.CanEdit(entry.Definition, trip)
                        && await ConfirmPopup.AskAsync(S["ReminderDelete"], entry.Definition.Title))
                    { var deleted = entry.Definition.Copy(); deleted.Deleted = true; await service.SaveAsync(trip, deleted); }
                    else if (result.Kind == "toggle" && service.CanEdit(entry.Definition, trip))
                    { var changed = entry.Definition.Copy(); changed.Enabled = !changed.Enabled; await service.SaveAsync(trip, changed); }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reminder popup: {ex.GetType().Name}");
            await ConfirmPopup.ShowMessageAsync(S["Reminders"], S[ex is InvalidOperationException && S[ex.Message] != ex.Message ? ex.Message : "ReminderSaveFailed"]);
        }
        finally { FlowGate.Release(); }
    }

    private async Task FinishAsync(ReminderAction action)
    {
        if (_closing) return;
        _closing = true;
        await CloseAsync(action);
    }
    private async void OnCloseClicked(object? sender, EventArgs e) => await FinishAsync(new("close"));
    private async void OnAddClicked(object? sender, EventArgs e) => await FinishAsync(new("add"));
    private async void OnMuteTripClicked(object? sender, EventArgs e) => await FinishAsync(new("tripmute"));
    private void OnDetailsClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ReminderListItem row }) return;
        _selected = row;
        DetailTitle.Text = row.Title; DetailBody.Text = row.Body;
        DetailInfo.Text = row.Details; DetailState.Text = row.State;
        EditActions.IsVisible = DetailDelete.IsVisible = row.CanEdit;
        DetailToggle.Text = row.ToggleText;
        DetailMute.Text = row.MuteText; DetailMute.IsEnabled = row.CanMute;
        ListPanel.IsVisible = false; DetailsPanel.IsVisible = true;
    }
    private void OnBackToListClicked(object? sender, EventArgs e)
    { DetailsPanel.IsVisible = false; ListPanel.IsVisible = true; _selected = null; }
    private Task DetailAction(string action) => _selected is { } row ? FinishAsync(new(action, row.Entry.Definition.Id)) : Task.CompletedTask;
    private async void OnDetailEditClicked(object? sender, EventArgs e) => await DetailAction("edit");
    private async void OnDetailToggleClicked(object? sender, EventArgs e) => await DetailAction("toggle");
    private async void OnDetailMuteClicked(object? sender, EventArgs e) => await DetailAction("mute");
    private async void OnDetailDeleteClicked(object? sender, EventArgs e) => await DetailAction("delete");
    private async void OnTimingClicked(object? sender, EventArgs e)
    { _service.Scheduler.OpenTimingSettings(); await FinishAsync(new("close")); }
    private async void OnPermissionClicked(object? sender, EventArgs e)
    {
        if (!await _service.Scheduler.RequestPermissionAsync()) AppInfo.Current.ShowSettingsUI();
        await FinishAsync(new("refresh"));
    }
}

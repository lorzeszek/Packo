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
    private readonly TripDTO _trip;
    private bool _tripMuted;
    private bool _busy;
    private bool _closing;
    private IReadOnlyList<ReminderEntry> _entries = [];
    private ReminderListItem? _selected;
    private static LocalizationResourceManager S => LocalizationResourceManager.Instance;

    private ReminderListPopup(Page page, TripReminderService service, TripDTO trip,
        IReadOnlyList<ReminderEntry> entries, bool tripMuted)
    {
        InitializeComponent();
        (_service, _trip) = (service, trip);
        PopupCard.WidthRequest = PopupCardSizing.GetWidth(page);
        PopupCard.MaximumHeightRequest = Math.Max(240, (page.Height > 0 ? page.Height : 740) - 70);
        ListScroll.MaximumHeightRequest = DetailsScroll.MaximumHeightRequest = Math.Clamp((page.Height > 0 ? page.Height : 740) - 330, 120, 420);
        TripLabel.Text = trip.Destination;
        ApplyEntries(entries, tripMuted);
    }

    private void ApplyEntries(IReadOnlyList<ReminderEntry> entries, bool tripMuted)
    {
        _entries = entries;
        var service = _service;
        var trip = _trip;
        _tripMuted = tripMuted;
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
                var popup = new ReminderListPopup(page, service, trip, entries, muted);
                var result = (await page.ShowPopupAsync<ReminderAction>(popup)).Result;
                entries = popup._entries;
                if (result is null || result.Kind == "close") return;
                if (result.Kind == "add")
                    snapshot = await ReminderEditorPopup.ShowAsync(page, service, trip, service.New(trip), entries)
                        ?? new ReminderEditorResult(entries, popup._tripMuted);
                else if (result.Kind == "edit" && entries.FirstOrDefault(x => x.Definition.Id == result.Id) is { } entry)
                    snapshot = await ReminderEditorPopup.ShowAsync(page, service, trip, entry.Definition, entries)
                        ?? new ReminderEditorResult(entries, popup._tripMuted);
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
        if (_closing || _busy) return;
        _closing = true;
        await CloseAsync(action);
    }
    private async void OnCloseClicked(object? sender, EventArgs e) => await FinishAsync(new("close"));
    private async void OnAddClicked(object? sender, EventArgs e) => await FinishAsync(new("add"));
    private async void OnMuteTripClicked(object? sender, EventArgs e) =>
        await RunMutationAsync(() => _service.SetMutedAsync(_trip, "*", !_tripMuted), showList: true);
    private void OnDetailsClicked(object? sender, EventArgs e)
    {
        if (_busy || _closing || sender is not Button { BindingContext: ReminderListItem row }) return;
        ShowDetails(row);
    }
    private void ShowDetails(ReminderListItem row)
    {
        _selected = row;
        DetailTitle.Text = row.Title; DetailBody.Text = row.Body;
        DetailInfo.Text = row.Details; DetailState.Text = row.State;
        EditActions.IsVisible = DetailDelete.IsVisible = row.CanEdit;
        DetailToggle.Text = row.ToggleText;
        DetailMute.Text = row.MuteText; DetailMute.IsEnabled = row.CanMute;
        ListPanel.IsVisible = DeletePanel.IsVisible = false; DetailsPanel.IsVisible = true;
    }
    private void OnBackToListClicked(object? sender, EventArgs e)
    { if (!_busy) ShowList(); }
    private void ShowList()
    { DetailsPanel.IsVisible = DeletePanel.IsVisible = false; ListPanel.IsVisible = true; _selected = null; }
    private async void OnDetailEditClicked(object? sender, EventArgs e)
    { if (_selected is { CanEdit: true } row) await FinishAsync(new("edit", row.Entry.Definition.Id)); }
    private async void OnDetailToggleClicked(object? sender, EventArgs e)
    {
        if (_selected is not { CanEdit: true } row) return;
        var changed = row.Entry.Definition.Copy(); changed.Enabled = !changed.Enabled;
        await RunMutationAsync(() => _service.SaveAsync(_trip, changed));
    }
    private async void OnDetailMuteClicked(object? sender, EventArgs e)
    {
        if (_selected is { CanMute: true } row)
            await RunMutationAsync(() => _service.SetMutedAsync(_trip, row.Entry.Definition.Id, !row.Entry.IndividuallyMuted));
    }
    private void OnDetailDeleteClicked(object? sender, EventArgs e)
    {
        if (_busy || _selected is not { CanEdit: true } row) return;
        MutationError.IsVisible = false;
        DeleteTitle.Text = row.Title;
        ListPanel.IsVisible = DetailsPanel.IsVisible = false; DeletePanel.IsVisible = true;
    }
    private void OnDeleteCancelClicked(object? sender, EventArgs e)
    { if (!_busy && _selected is { } row) { MutationError.IsVisible = false; ShowDetails(row); } }
    private async void OnDeleteConfirmClicked(object? sender, EventArgs e)
    {
        if (_selected is not { CanEdit: true } row) return;
        var deleted = row.Entry.Definition.Copy(); deleted.Deleted = true;
        await RunMutationAsync(() => _service.SaveAsync(_trip, deleted), showList: true);
    }

    private async Task RunMutationAsync(Func<Task> operation, bool showList = false)
    {
        if (_busy || _closing) return;
        _busy = true;
        MutationError.IsVisible = false;
        InteractionPanels.IsEnabled = false;
        UpdatingPanel.IsVisible = UpdatingIndicator.IsRunning = true;
        try
        {
            await operation();
            await ReloadAsync(showList);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reminder action: {ex.GetType().Name}");
            // A conflict may have downloaded a newer definition. Keep subsequent
            // editing/retries bound to that version without closing the popup.
            try { await ReloadAsync(showList: true); }
            catch (Exception refreshError) { System.Diagnostics.Debug.WriteLine($"Reminder reload: {refreshError.GetType().Name}"); }
            MutationError.Text = S[ex is InvalidOperationException && S[ex.Message] != ex.Message ? ex.Message : "ReminderSaveFailed"];
            MutationError.IsVisible = true;
        }
        finally
        {
            _busy = false;
            InteractionPanels.IsEnabled = true;
            UpdatingPanel.IsVisible = UpdatingIndicator.IsRunning = false;
        }
    }

    private async Task ReloadAsync(bool showList)
    {
        var selectedId = _selected?.Entry.Definition.Id;
        var entries = await _service.GetAsync(_trip);
        var muted = await _service.IsTripMutedAsync(_trip);
        ApplyEntries(entries, muted);
        if (!showList && entries.FirstOrDefault(x => x.Definition.Id == selectedId) is { } entry)
            ShowDetails(Item(_service, _trip, entry));
        else ShowList();
    }
    private async void OnTimingClicked(object? sender, EventArgs e)
    { _service.Scheduler.OpenTimingSettings(); await FinishAsync(new("close")); }
    private async void OnPermissionClicked(object? sender, EventArgs e)
    {
        await RunMutationAsync(async () =>
        { if (!await _service.Scheduler.RequestPermissionAsync()) AppInfo.Current.ShowSettingsUI(); }, showList: true);
    }
}

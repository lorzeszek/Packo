using Packo.Models.DTO;

namespace Packo.Helpers;

public sealed record ReminderTripTarget(string AccountId, int LocalTripId, int RemoteTripId,
    string? ReminderId = null, string? Title = null, string? Body = null)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(AccountId) && LocalTripId >= 0 && RemoteTripId >= 0
        && (LocalTripId > 0 || RemoteTripId > 0);

    public bool Matches(TripDTO trip) => trip.LocalUserId == AccountId && !trip.IsInTrash
        && (RemoteTripId > 0 ? trip.RemoteTripId == RemoteTripId : trip.LocalTripId == LocalTripId);
}

/// <summary>Defers notification taps until startup/modal work ends and validates the active account.</summary>
public sealed class ReminderNavigationQueue(Func<bool> canNavigate, Func<string?> currentAccount,
    Func<ReminderTripTarget, Task<TripDTO?>> resolveTrip, Func<Task> openTripList,
    Func<ReminderTripTarget, TripDTO, Task<bool>> showReminder, Func<TripDTO, Task> openDetails, Func<Task> unavailable)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record PendingTap(ReminderTripTarget Target, bool DetailsOnly = false);
    private PendingTap? _pending;
    public bool HasPending => _pending is not null;

    public void Receive(ReminderTripTarget target)
    {
        if (target.IsValid) _pending = new(target);
    }

    public async Task ProcessAsync()
    {
        if (!canNavigate() || !await _gate.WaitAsync(0)) return;
        try
        {
            while (canNavigate() && _pending is { } pending)
            {
                var target = pending.Target;
                if (currentAccount() != target.AccountId) { _pending = null; continue; }
                TripDTO? trip;
                try { trip = await resolveTrip(target); }
                catch
                {
                    if (ReferenceEquals(_pending, pending)) _pending = null;
                    throw;
                }
                if (!ReferenceEquals(_pending, pending)) continue;
                if (currentAccount() != target.AccountId) { _pending = null; continue; }
                if (!canNavigate()) return;
                _pending = null;
                if (trip is null || !target.Matches(trip))
                {
                    await openTripList();
                    if (currentAccount() != target.AccountId || _pending is not null) continue;
                    if (!canNavigate()) { _pending = pending; return; }
                    await unavailable();
                }
                else if (pending.DetailsOnly) await openDetails(trip);
                else
                {
                    await openTripList();
                    if (currentAccount() != target.AccountId || _pending is not null) continue;
                    if (!canNavigate()) { _pending = pending; return; }
                    var goToDetails = await showReminder(target, trip);
                    if (goToDetails && currentAccount() == target.AccountId)
                        _pending ??= new(target, DetailsOnly: true);
                }
            }
        }
        finally { _gate.Release(); }
    }
}

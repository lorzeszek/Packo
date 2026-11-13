using Packo.Models.DTO;

namespace Packo.Helpers;

public sealed record ReminderTripTarget(string AccountId, int LocalTripId, int RemoteTripId)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(AccountId) && LocalTripId >= 0 && RemoteTripId >= 0
        && (LocalTripId > 0 || RemoteTripId > 0);

    public bool Matches(TripDTO trip) => trip.LocalUserId == AccountId && !trip.IsInTrash
        && (RemoteTripId > 0 ? trip.RemoteTripId == RemoteTripId : trip.LocalTripId == LocalTripId);
}

/// <summary>Defers notification taps until startup/modal work ends and validates the active account.</summary>
public sealed class ReminderNavigationQueue(Func<bool> canNavigate, Func<string?> currentAccount,
    Func<ReminderTripTarget, Task<TripDTO?>> resolveTrip, Func<TripDTO, Task> navigate, Func<Task> unavailable)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReminderTripTarget? _pending;
    public bool HasPending => _pending is not null;

    public void Receive(ReminderTripTarget target)
    {
        if (target.IsValid) _pending = target;
    }

    public async Task ProcessAsync()
    {
        if (!canNavigate() || !await _gate.WaitAsync(0)) return;
        try
        {
            while (canNavigate() && _pending is { } target)
            {
                if (currentAccount() != target.AccountId) { _pending = null; continue; }
                TripDTO? trip;
                try { trip = await resolveTrip(target); }
                catch
                {
                    if (ReferenceEquals(_pending, target)) _pending = null;
                    throw;
                }
                if (!ReferenceEquals(_pending, target)) continue;
                if (currentAccount() != target.AccountId) { _pending = null; continue; }
                if (!canNavigate()) return;
                _pending = null;
                if (trip is null || !target.Matches(trip)) await unavailable();
                else await navigate(trip);
            }
        }
        finally { _gate.Release(); }
    }
}

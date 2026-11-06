using Packo.Models.DTO;

namespace Packo.Messages;

public sealed record PackingProgressChangedMessage(int LocalTripId, int? RemoteTripId, string LocalUserId, string PackingSummary)
{
    public bool MatchesTrip(TripDTO trip) => RemoteTripId > 0 && trip.RemoteTripId > 0
        ? trip.RemoteTripId == RemoteTripId
        : LocalTripId > 0 && trip.LocalTripId == LocalTripId && trip.LocalUserId == LocalUserId;
}

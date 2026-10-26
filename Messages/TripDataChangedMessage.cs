namespace Packo.Messages;

public sealed class TripDataChangedMessage
{
    public int? LocalTripId { get; init; }
    public int? RemoteTripId { get; init; }
    public string? LocalUserId { get; init; }
    public string? PackingSummary { get; init; }
}

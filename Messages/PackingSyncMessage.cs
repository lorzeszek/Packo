namespace Packo.Messages;

public sealed record PackingSyncMessage(int RemoteTripId, bool Conflict = false, bool AccessRevoked = false);

// Host-only substitutes for MAUI connectivity and unrelated application services.
public enum NetworkAccess { None, Internet }
public sealed class ConnectivityChangedEventArgs : EventArgs
{
    public NetworkAccess NetworkAccess { get; init; }
}
public sealed class Connectivity
{
    public static Connectivity Current { get; } = new();
    public NetworkAccess NetworkAccess { get; set; }
    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;
}
namespace Packo.Services.Interfaces
{
    public interface ISessionService
    {
        string? LocalUserId { get; }
        string? UserId { get; }
        bool IsAuthenticated { get; }
    }
}
namespace Packo.Repositories.Interfaces
{
    public interface ITripRepository
    {
        Task<LocalTrip?> GetLocalTripAsync(int id, string user);
        Task SyncPendingChangesAsync();
        Task<IReadOnlyList<Packo.Repositories.Models.TripWithStats>> GetActiveTripsWithStatsAsync();
    }
    public record LocalTrip(int RemoteTripId);
}

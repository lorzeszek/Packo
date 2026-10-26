// Host-only substitutes for MAUI dispatch and services unrelated to weather.
using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;

public static class MainThread
{
    public static Task InvokeOnMainThreadAsync(Action action) { action(); return Task.CompletedTask; }
    public static void BeginInvokeOnMainThread(Action action) => action();
}

namespace Packo.Services.Interfaces
{
    public interface ISessionService
    {
        string? UserId { get; }
        string? LocalUserId { get; }
    }
    public interface ILocalUserService { }
    public interface ISupabaseService { }
}
namespace Packo.Interfaces { public interface IGoogleAuthService { } }
namespace Packo.Repositories.Interfaces
{
    public interface IPackingItemRepository { }
    public interface ITripRepository { Task<IReadOnlyList<string>> GetActiveDestinationsAsync(); }
}
namespace Packo.ViewModels
{
    public abstract class BaseViewModel : ObservableObject
    {
        public ISessionService Session { get; }
        protected BaseViewModel(ILocalUserService localUserService, ISupabaseService supabase,
            ISessionService session, IPackingItemRepository packing, ITripRepository trips,
            IGoogleAuthService google) => Session = session;
    }
}
namespace Packo.Services
{
    public sealed class LocalizationResourceManager
    {
        public static LocalizationResourceManager Instance { get; } = new();
        public string this[string key] => key == "WeatherDestinationsUnavailable"
            ? "Forecast unavailable for: {0}" : key;
    }
}

// Host-only substitutes for MAUI navigation and unrelated application services.
using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.Models.DTO;
using Packo.Repositories.Models;

public static class MainThread
{
    public static void BeginInvokeOnMainThread(Action action) => action();
}

public class ShellNavigationQueryParameters : Dictionary<string, object> { }
public sealed class Command(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
public sealed class Shell
{
    public static Shell Current { get; } = new();
    public string? Route { get; private set; }
    public IDictionary<string, object>? Query { get; private set; }
    public Func<Task<bool>> Confirm { get; set; } = () => Task.FromResult(false);
    public string? ConfirmationMessage { get; private set; }
    public int ConfirmationCount { get; private set; }
    public string? ErrorMessage { get; private set; }
    public Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
    {
        ConfirmationMessage = message;
        ConfirmationCount++;
        return Confirm();
    }
    public Task DisplayAlertAsync(string title, string message, string cancel)
    {
        ErrorMessage = message;
        return Task.CompletedTask;
    }
    public Task GoToAsync(string route, IDictionary<string, object>? query = null)
    {
        Route = route;
        Query = query;
        return Task.CompletedTask;
    }
}
public sealed class Application
{
    public static Application Current { get; } = new();
    public object MainPage { get; } = new();
}
namespace CommunityToolkit.Maui.Extensions
{
    public record PopupResult<T>(T Result);
    public static class PopupExtensions
    {
        public static Task<PopupResult<T>> ShowPopupAsync<T>(this object page, object popup)
            => Task.FromResult(new PopupResult<T>(default!));
    }
}
namespace Packo.Popups
{
    public class ConfirmPopup(string title, string message) { }
}

namespace Packo.Services.Interfaces
{
    public interface ISessionService
    {
        string? LocalUserId { get; }
        bool GlobalIsBusy { get; }
        bool IsAuthenticated => false;
    }
    public interface ILocalUserService { }
    public interface ISupabaseService { AuthClient Client { get; } }
    public sealed class AuthClient
    {
        public AuthClient Auth => this;
        public Task SignOut() => Task.CompletedTask;
    }
    public interface IWeatherPreloadService { void Invalidate(); }
}
namespace Packo.Interfaces { public interface IGoogleAuthService { } }
namespace Packo.Repositories.Interfaces
{
    public interface IPackingItemRepository
    {
        Task UnsubscribeFromPackingItemChangesAsync();
    }
    public interface ITripRepository
    {
        event Action<TripDTO, string>? TripChanged;
        Task DeleteTripAsync(TripDTO trip);
        Task DeleteLocalTrips() => Task.CompletedTask;
        Task UnsubscribeFromTripChangesAsync() => Task.CompletedTask;
        Task SyncPendingChangesAsync() => Task.CompletedTask;
        Task StartRealtimeAsync() => Task.CompletedTask;
        Task<bool> IsChannelCreatedAsync() => Task.FromResult(false);
    }
}
namespace Packo.Views
{
    public class PackingListPage { }
    public class TripSetupPage { }
}
namespace Packo.ViewModels
{
    public abstract class BaseViewModel : ObservableObject
    {
        public ISessionService Session { get; }
        public string Title { get; set; } = string.Empty;
        protected readonly ITripRepository _tripRepository;
        protected readonly IPackingItemRepository _packingItemRepository;
        protected readonly ISupabaseService _supabase;
        public Subscription? _subscription;
        protected BaseViewModel(ILocalUserService localUserService, ISupabaseService supabase,
            ISessionService session, IPackingItemRepository packing, ITripRepository trips,
            IGoogleAuthService google)
        {
            Session = session;
            _tripRepository = trips;
            _packingItemRepository = packing;
            _supabase = supabase;
        }
        protected virtual Task OnNavigatedToAsync(IDictionary<string, object> query) => Task.CompletedTask;
        public virtual Task OnNavigatedFromAsync(IDictionary<string, object> query) => Task.CompletedTask;
    }
    public sealed class Subscription { public void Unsubscribe() { } }
}
namespace Packo.Services
{
    public sealed class LocalizationResourceManager
    {
        public static LocalizationResourceManager Instance { get; } = new();
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string this[string key] => key == "DeleteTripConfirm" ? "Delete trip {0}?" : key;
        public void ChangeLanguage() => PropertyChanged?.Invoke(this, new(null));
    }
}

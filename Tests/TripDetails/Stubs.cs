// Host-only substitutes for MAUI navigation and unrelated application services.
using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;

public static class MainThread
{
    public static void BeginInvokeOnMainThread(Action action) => action();
}

public class ShellNavigationQueryParameters : Dictionary<string, object> { }
public sealed class Shell
{
    public static Shell Current { get; } = new();
    public string? Route { get; private set; }
    public IDictionary<string, object>? Query { get; private set; }
    public Task GoToAsync(string route, IDictionary<string, object>? query = null)
    {
        Route = route;
        Query = query;
        return Task.CompletedTask;
    }
}

namespace Packo.Services.Interfaces
{
    public interface ISessionService
    {
        string? LocalUserId { get; }
        bool GlobalIsBusy { get; }
    }
    public interface ILocalUserService { }
    public interface ISupabaseService { }
}
namespace Packo.Interfaces { public interface IGoogleAuthService { } }
namespace Packo.Repositories.Interfaces
{
    public interface IPackingItemRepository { }
    public interface ITripRepository { }
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
        protected BaseViewModel(ILocalUserService localUserService, ISupabaseService supabase,
            ISessionService session, IPackingItemRepository packing, ITripRepository trips,
            IGoogleAuthService google) => Session = session;
        protected virtual Task OnNavigatedToAsync(IDictionary<string, object> query) => Task.CompletedTask;
    }
}
namespace Packo.Services
{
    public sealed class LocalizationResourceManager
    {
        public static LocalizationResourceManager Instance { get; } = new();
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string this[string key] => key;
        public void ChangeLanguage() => PropertyChanged?.Invoke(this, new(null));
    }
}

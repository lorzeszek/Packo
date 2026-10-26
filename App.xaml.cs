using Packo.Helpers;
using Packo.Models.SQLite;
using Packo.Services;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using Packo.Views;
using SQLite;
using System.ComponentModel;

namespace Packo
{
    public partial class App : Application
    {
        private readonly ISessionService _sessionService;
        private readonly SQLiteAsyncConnection _db;
        private readonly ILocalUserService _localUserService;
        private readonly ITripListPreloadService _tripListPreloadService;
        private readonly IWeatherPreloadService _weatherPreloadService;
        private readonly IDocumentPreloadService _documentPreloadService;
        private LoadingPage? _loadingPage;
        //private Task _loadingModalTask = Task.CompletedTask;

        public App(
            ISessionService sessionService,
            SQLiteAsyncConnection db,
            ILocalUserService localUserService,
            ITripListPreloadService tripListPreloadService,
            IWeatherPreloadService weatherPreloadService,
            IDocumentPreloadService documentPreloadService)
        {
            InitializeComponent();
            LocalizationResourceManager.Instance.ApplySavedLanguage();
            AppThemeManager.ApplySavedTheme();
            _sessionService = sessionService;
            _db = db;
            _localUserService = localUserService;
            _tripListPreloadService = tripListPreloadService;
            _weatherPreloadService = weatherPreloadService;
            _documentPreloadService = documentPreloadService;

            if (_sessionService is INotifyPropertyChanged npc)
            {
                npc.PropertyChanged += OnSessionPropertyChanged;
            }
        }

        //private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        //{
        //    if (e.PropertyName != nameof(ISessionService.GlobalIsBusy))
        //        return;

        //    MainThread.BeginInvokeOnMainThread(async () => await UpdateLoadingModalAsync(_sessionService.GlobalIsBusy));
        //}

        private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ISessionService.GlobalIsBusy))
                return;

            var isBusy = _sessionService.GlobalIsBusy;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await UpdateLoadingModalAsync(isBusy);
            });
        }

        private readonly SemaphoreSlim _loadingModalLock = new(1, 1);

        private async Task UpdateLoadingModalAsync(bool show)
        {
            if (MainPage is null)
                return;

            await _loadingModalLock.WaitAsync();

            try
            {
                var navigation = Shell.Current?.Navigation ?? MainPage.Navigation;

                if (show)
                {
                    if (_loadingPage != null)
                        return;

                    _loadingPage = new LoadingPage();

                    await navigation.PushModalAsync(_loadingPage, false);
                    return;
                }

                if (_loadingPage is null)
                    return;

                try
                {
                    if (navigation.ModalStack.LastOrDefault() == _loadingPage)
                    {
                        await navigation.PopModalAsync(false);
                    }
                    else if (navigation.ModalStack.Contains(_loadingPage))
                    {
                        await _loadingPage.Navigation.PopModalAsync(false);
                    }
                }
                finally
                {
                    _loadingPage = null;
                }
            }
            finally
            {
                _loadingModalLock.Release();
            }
        }

        //private async Task UpdateLoadingModalAsync(bool show)
        //{
        //    if (MainPage is null)
        //        return;

        //    var navigation = Shell.Current?.Navigation ?? MainPage.Navigation;

        //    if (show)
        //    {
        //        if (_loadingPage != null)
        //            return;

        //        _loadingPage = new LoadingPage();
        //        await navigation.PushModalAsync(_loadingPage, false);
        //        return;
        //    }

        //    if (_loadingPage is null)
        //        return;

        //    try
        //    {
        //        if (navigation.ModalStack.LastOrDefault() == _loadingPage)
        //        {
        //            await navigation.PopModalAsync(false);
        //        }
        //        else if (navigation.ModalStack.Contains(_loadingPage))
        //        {
        //            await _loadingPage.Navigation.PopModalAsync(false);
        //        }
        //    }
        //    catch (Exception)
        //    {
        //    }
        //    finally
        //    {
        //        _loadingPage = null;
        //    }
        //}

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var bootstrapPage = new StartupPage();
            var window = new Window(bootstrapPage);
            var started = false;
            bootstrapPage.Loaded += async (_, _) =>
            {
                if (started)
                    return;
                started = true;
                await InitializeAndNavigateAsync(window, bootstrapPage);
            };
            bootstrapPage.RetryRequested += async (_, _) => await InitializeAndNavigateAsync(window, bootstrapPage);
            return window;
        }

        private bool _startupInitialized;
        private bool _startupRunning;

        private async Task InitializeAndNavigateAsync(Window window, StartupPage startupPage)
        {
            if (_startupRunning)
                return;

            _startupRunning = true;
            try
            {
                startupPage.ShowLoading("StartupPreparing");
                if (!_startupInitialized)
                {
                    await InitializeStorageAndSessionAsync(startupPage);
                    _startupInitialized = true;
                }

                startupPage.ShowLoading("StartupLoadingTrips");
                // Home and Trips consume this same cached result. Await the real
                // request here: optional PrefetchAsync deliberately swallows errors.
                await _tripListPreloadService.GetTripsAsync().WaitAsync(TimeSpan.FromSeconds(20));

                await MainThread.InvokeOnMainThreadAsync(() => window.Page = new AppShell());

                // Only the data needed for the first screen delays app startup.
                _ = _weatherPreloadService.PrefetchAsync();
                _ = _documentPreloadService.PrefetchAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"App startup failed: {ex}");
                startupPage.ShowError();
            }
            finally
            {
                _startupRunning = false;
            }
        }

        private async Task InitializeStorageAndSessionAsync(StartupPage startupPage)
        {
            //SecureStorage.Remove("access_token");
            //SecureStorage.Remove("refresh_token");

            //await _db.ExecuteAsync("DROP TABLE IF EXISTS SQLiteTrip");
            await _db.CreateTableAsync<SQLiteTrip>();
            await EnsureTripColumnAsync("AdultsCount", "INTEGER NOT NULL DEFAULT 1");
            await EnsureTripColumnAsync("ChildrenAgesJson", "TEXT NOT NULL DEFAULT '[]'");
            await EnsureTripColumnAsync("SelectedTransportTypesJson", "TEXT NOT NULL DEFAULT '[]'");
            await EnsureTripColumnAsync("TripType", "TEXT NOT NULL DEFAULT 'Leisure'");

            //await _db.ExecuteAsync("DROP TABLE IF EXISTS SQLitePackingItem");
            await Packo.Repositories.Local.LocalPackingItemRepository.EnsureSchemaAsync(_db);

            //await _db.ExecuteAsync("DROP TABLE IF EXISTS SQLiteTripDocument");
            await _db.CreateTableAsync<SQLiteTripDocument>();

            //await _db.ExecuteAsync("DROP TABLE IF EXISTS SQLiteUser");
            await _db.CreateTableAsync<SQLiteUser>();

            //await _db.ExecuteAsync("DROP TABLE IF EXISTS SQLitePendingTripChange");
            await _db.CreateTableAsync<SQLitePendingTripChange>();

            //await _db.ExecuteAsync("DROP TABLE IF EXISTS SQLitePendingPackingItemChange");
            await _db.CreateTableAsync<SQLitePendingPackingItemChange>();

            //await _localUserService.GetOrCreateAsync();

            var localUser = await _localUserService.GetOrCreateLocalUserAsync();

            if (localUser != null)
            {
                _sessionService.SetLocalUser(localUser.LocalUserId);
            }

            startupPage.ShowLoading("StartupRestoringSession");
            await _sessionService.InitializeAsync();
        }

        private async Task EnsureTripColumnAsync(string columnName, string columnDefinition)
        {
            var columns = await _db.QueryAsync<SQLiteColumnInfo>("PRAGMA table_info(SQLiteTrip)");
            if (columns.Any(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)))
                return;

            await _db.ExecuteAsync($"ALTER TABLE SQLiteTrip ADD COLUMN {columnName} {columnDefinition}");
        }

        private sealed class SQLiteColumnInfo
        {
            public string Name { get; set; } = string.Empty;
        }
    }
}

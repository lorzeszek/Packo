using Packo.Helpers;
using Packo.Models.SQLite;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Repositories.Interfaces;
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
        private readonly ITripRepository _trips;
        private readonly IPackingItemRepository _packingItems;
        private readonly TripInvitationService _invitations;
        private LoadingPage? _loadingPage;
        //private Task _loadingModalTask = Task.CompletedTask;

        public App(
            ISessionService sessionService,
            SQLiteAsyncConnection db,
            ILocalUserService localUserService,
            ITripListPreloadService tripListPreloadService,
            IWeatherPreloadService weatherPreloadService,
            IDocumentPreloadService documentPreloadService,
            ITripRepository trips,
            IPackingItemRepository packingItems,
            TripInvitationService invitations)
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
            _trips = trips;
            _packingItems = packingItems;
            _invitations = invitations;
            _invitations.InviteReceived += (_, _) =>
            {
                if (_startupRunning && Windows.FirstOrDefault()?.Page is StartupPage startupPage)
                    startupPage.ShowLoading("OpeningTripInvite");
            };
            WeakReferenceMessenger.Default.Register<App, LoginCompletedMessage>(this, (app, _) =>
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await app.ResumePendingChangesAsync();
                    await app._invitations.ProcessPendingAsync();
                }));
            Connectivity.Current.ConnectivityChanged += (_, args) =>
            {
                if (args.NetworkAccess == NetworkAccess.Internet)
                    MainThread.BeginInvokeOnMainThread(async () => await _invitations.ProcessPendingAsync());
            };

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
                if (!isBusy)
                    await _invitations.ProcessPendingAsync();
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
            window.Activated += (_, _) => _invitations.SetWindowActive(true);
            window.Deactivated += (_, _) => _invitations.SetWindowActive(false);
            window.Resumed += async (_, _) =>
            {
                if (!_startupInitialized)
                    return;

                var recovered = await _sessionService.TryRecoverSessionAsync();
                WeakReferenceMessenger.Default.Send(new PackingSyncMessage(0));
                if (!recovered)
                {
                    _ = ResumePendingChangesAsync();
                    _ = _invitations.ProcessPendingAsync();
                }
            };
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
                ShowStartupLoading(startupPage, "StartupPreparing");
                if (!_startupInitialized)
                {
                    await InitializeStorageAndSessionAsync(startupPage);
                    _startupInitialized = true;
                }
                else
                {
                    await _sessionService.TryRecoverSessionAsync(forceRefresh: true);
                }

                if (!_invitations.HasPendingInvite)
                {
                    ShowStartupLoading(startupPage, "StartupLoadingTrips");
                    // The normal launch awaits the Trips request. Opening an invite can
                    // let the Trips page load that data while the prompt is already available.
                    await _tripListPreloadService.GetTripsAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var shell = new AppShell();
                    _invitations.AttachShell(shell);
                    window.Page = shell;
                });

                // Only the data needed for the first screen delays app startup.
                _ = ResumePendingChangesAsync();
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

        private async Task ResumePendingChangesAsync()
        {
            if (!_sessionService.IsAuthenticated || Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                return;

            try
            {
                var hasPendingTrips = await _db.Table<SQLitePendingTripChange>()
                    .Where(change => change.LocalUserId == _sessionService.LocalUserId).CountAsync() > 0;
                await _trips.SyncPendingChangesAsync();
                await _packingItems.SyncPendingChangesAsync();
                if (hasPendingTrips)
                    WeakReferenceMessenger.Default.Send(new TripDataChangedMessage());
                await _packingItems.StartRealtimeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
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

            ShowStartupLoading(startupPage, "StartupRestoringSession");
            await _sessionService.InitializeAsync();
        }

        private void ShowStartupLoading(StartupPage page, string statusKey) =>
            page.ShowLoading(_invitations.HasPendingInvite ? "OpeningTripInvite" : statusKey);

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

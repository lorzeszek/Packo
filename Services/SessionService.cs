using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Services.Interfaces;
using Supabase.Gotrue;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Packo.Services
{
    public class SessionService : ISessionService, INotifyPropertyChanged
    {
        private bool _globalIsBusy;
        public bool GlobalIsBusy
        {
            get => _globalIsBusy;
            set
            {
                if (_globalIsBusy == value)
                    return;
                _globalIsBusy = value;
                OnPropertyChanged();
                GlobalIsBusyChanged?.Invoke(this, value);
            }
        }

        public event EventHandler<bool>? GlobalIsBusyChanged;
        public readonly ISupabaseService _supabase;
        public readonly ILocalUserService _localUserService;
        private const string CachedAccountKey = "cached_account_id";
        private readonly SemaphoreSlim _sessionGate = new(1, 1);

        public bool IsAuthenticated => UserId is not null
            && _supabase.Client.Auth.CurrentSession is { } session
            && session.User?.Id == UserId && !session.Expired();
        public bool HasLocalUser => !string.IsNullOrEmpty(LocalUserId); // SQLite

        /// <summary>
        /// The profile image supplied by the identity provider. Google stores this in
        /// the user's metadata, although the exact key differs between providers.
        /// </summary>
        public string? AvatarUrl => GetProfileValue("avatar_url", "picture");

        public bool HasAvatar => Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        public string UserInitial
        {
            get
            {
                var name = GetProfileValue("full_name", "name") ?? User?.Email;
                return string.IsNullOrWhiteSpace(name)
                    ? "?"
                    : name.Trim()[0].ToString().ToUpperInvariant();
            }
        }

        public string? LocalUserId { get; private set; }

        private string? _userId;
        public string? UserId
        {
            get => _userId;
            private set
            {
                if (_userId != value)
                {
                    _userId = value;
                    OnPropertyChanged();
                }
            }
        }

        private User? _user;
        public User? User
        {
            get => _user;
            set
            {
                if (_user != value)
                {
                    _user = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsAuthenticated));
                    OnPropertyChanged(nameof(AvatarUrl));
                    OnPropertyChanged(nameof(HasAvatar));
                    OnPropertyChanged(nameof(UserInitial));
                }
            }
        }

        public SessionService(ISupabaseService supabase, ILocalUserService localUserService)
        {
            _supabase = supabase;
            _localUserService = localUserService;
        }

        public async Task InitializeAsync()
        {
            await TryRestoreSessionAsync(allowOfflineAccount: true);
            Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

            _supabase.Client.Auth.AddStateChangedListener(async (_, state) =>
            {
                if (state != Constants.AuthState.TokenRefreshed)
                    return;

                await _sessionGate.WaitAsync();
                try
                {
                    if (_supabase.Client.Auth.CurrentSession is { } session && UserId is not null && session.User?.Id == UserId)
                        await SaveTokensAsync(session);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
                finally { _sessionGate.Release(); }
            });
        }

        private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
                await TryRecoverSessionAsync();
        }

        public Task<bool> TryRecoverSessionAsync(bool forceRefresh = false)
        {
            if (UserId is null || GlobalIsBusy || Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                return Task.FromResult(false);

            return TryRestoreSessionAsync(forceRefresh: forceRefresh);
        }

        public void SetLocalUser(string localUserId)
        {
            LocalUserId = localUserId;
        }

        public async Task SetAuthenticatedUserAsync(User user, bool attachGuestDrafts = false)
        {
            await _sessionGate.WaitAsync();
            try { await SetAuthenticatedUserCoreAsync(user, attachGuestDrafts); }
            finally { _sessionGate.Release(); }
        }

        private async Task SetAuthenticatedUserCoreAsync(User user, bool attachGuestDrafts = false)
        {
            if (_supabase.Client.Auth.CurrentSession is not { } session || session.User?.Id != user.Id)
                return;
            var local = await _localUserService.GetOrCreateAccountAsync(user.Id, attachGuestDrafts);
            await SaveTokensAsync(session);
            await SecureStorage.SetAsync(CachedAccountKey, user.Id);
            await MainThread.InvokeOnMainThreadAsync(() => SetLocalAccount(user, local.LocalUserId));
        }

        private static async Task SaveTokensAsync(Session session)
        {
            if (!string.IsNullOrEmpty(session.AccessToken))
                await SecureStorage.SetAsync("access_token", session.AccessToken);
            if (!string.IsNullOrEmpty(session.RefreshToken))
                await SecureStorage.SetAsync("refresh_token", session.RefreshToken);
        }

        private void SetLocalAccount(User user, string localId)
        {
            SetLocalUser(localId);
            UserId = user.Id;
            User = user;
            OnPropertyChanged(nameof(IsAuthenticated));
        }

        public async Task SwitchToGuestAsync()
        {
            await _sessionGate.WaitAsync();
            try
            {
                await _supabase.Client.Auth.SignOut();
                SecureStorage.Remove("access_token");
                SecureStorage.Remove("refresh_token");
                SecureStorage.Remove(CachedAccountKey);
                var guest = await _localUserService.GetOrCreateLocalUserAsync();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    SetLocalUser(guest.LocalUserId);
                    UserId = null;
                    User = null;
                });
            }
            finally { _sessionGate.Release(); }
        }

        private string? GetProfileValue(params string[] keys)
        {
            if (User is null)
                return null;

            foreach (var key in keys)
            {
                if (User.UserMetadata.TryGetValue(key, out var metadataValue)
                    && metadataValue is not null
                    && !string.IsNullOrWhiteSpace(metadataValue.ToString()))
                {
                    return metadataValue.ToString();
                }

                var identityValue = User.Identities?
                    .FirstOrDefault(identity => identity.Provider == "google")?
                    .IdentityData?
                    .GetValueOrDefault(key);

                if (identityValue is not null && !string.IsNullOrWhiteSpace(identityValue.ToString()))
                    return identityValue.ToString();
            }

            return null;
        }

        private async Task<bool> TryRestoreSessionAsync(bool allowOfflineAccount = false, bool forceRefresh = false)
        {
            var auth = _supabase.Client.Auth;
            var account = UserId;
            var previousAccessToken = auth.CurrentSession?.AccessToken;
            string? accessToken = null;
            var restored = false;
            await _sessionGate.WaitAsync();
            try
            {
                if (!allowOfflineAccount && (GlobalIsBusy || UserId != account))
                    return false;

                var current = auth.CurrentSession;
                if (IsAuthenticated && (!forceRefresh || current?.AccessToken != previousAccessToken))
                    return false;

                if (current is not null && current.User?.Id != account)
                    return false;

                accessToken = current?.AccessToken ?? await SecureStorage.GetAsync("access_token");
                if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet || string.IsNullOrWhiteSpace(accessToken))
                    return false;

                var refreshToken = current?.RefreshToken ?? await SecureStorage.GetAsync("refresh_token");
                if (string.IsNullOrWhiteSpace(refreshToken))
                    return false;

                // In GoTrue 6.0.3, RefreshSession rejects expired access tokens.
                // The refresh-token sign-in also works on an expired in-memory session.
                var session = current is not null
                    ? await auth.SignIn(Constants.SignInType.RefreshToken, refreshToken)
                    : await auth.SetSession(accessToken, refreshToken, forceAccessTokenRefresh: true);
                if (session?.User is { } user && (account is null || user.Id == account))
                {
                    await SetAuthenticatedUserCoreAsync(user);
                    restored = IsAuthenticated;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
            finally
            {
                try
                {
                    if (allowOfflineAccount && User is null)
                    {
                        var cachedId = await SecureStorage.GetAsync(CachedAccountKey) ?? ReadCachedSubject(accessToken);
                        if (cachedId is not null)
                        {
                            var local = await _localUserService.GetOrCreateAccountAsync(cachedId);
                            await MainThread.InvokeOnMainThreadAsync(() => SetLocalAccount(new User { Id = cachedId }, local.LocalUserId));
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
                finally { _sessionGate.Release(); }
            }

            if (restored)
                await MainThread.InvokeOnMainThreadAsync(() => WeakReferenceMessenger.Default.Send(new LoginCompletedMessage()));
            else if (!IsAuthenticated)
                await MainThread.InvokeOnMainThreadAsync(() => OnPropertyChanged(nameof(IsAuthenticated)));

            return restored;
        }

        // Previous app versions saved tokens without a separate offline account ID.
        private static string? ReadCachedSubject(string? token)
        {
            var segments = token?.Split('.');
            if (segments?.Length != 3)
                return null;
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.TryGetProperty("sub", out var subject) && Guid.TryParse(subject.GetString(), out var id)
                ? id.ToString() : null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

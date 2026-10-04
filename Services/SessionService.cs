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

        public bool IsAuthenticated => User != null;
        public bool HasLocalUser => !string.IsNullOrEmpty(LocalUserId); // SQLite

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
            await TryRestoreSessionAsync();

            _supabase.Client.Auth.AddStateChangedListener(async (sender, e) =>
            {
                var currentSession = _supabase.Client.Auth.CurrentSession;

                if (currentSession != null)
                {
                    if (!string.IsNullOrEmpty(currentSession.AccessToken))
                        await SecureStorage.SetAsync("access_token", currentSession.AccessToken);

                    if (!string.IsNullOrEmpty(currentSession.RefreshToken))
                        await SecureStorage.SetAsync("refresh_token", currentSession.RefreshToken);

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        SetUser(currentSession.User);

                        _localUserService.LinkSupabaseUserAsync(currentSession.User.Id);
                    });
                }
                else
                {
                    SecureStorage.Remove("access_token");
                    SecureStorage.Remove("refresh_token");

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        ClearUser();
                    });
                }
            });
        }

        public void SetLocalUser(string localUserId)
        {
            LocalUserId = localUserId;
        }

        public void SetUser(User? user)
        {
            User = user;
            UserId = user?.Id;
        }

        public void ClearUser()
        {
            User = null;
            UserId = null;
        }

        private async Task TryRestoreSessionAsync()
        {
            try
            {
                var refreshToken = await SecureStorage.GetAsync("refresh_token");
                var accessToken = await SecureStorage.GetAsync("access_token");

                if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
                    return;

                // Must be awaited: this is what actually refreshes an expired access_token
                // using the refresh_token. Previously it was fire-and-forget, which raced
                // against GetUser(accessToken) below using the (possibly expired) stale token.
                var session = await _supabase.Client.Auth.SetSession(accessToken, refreshToken, true);

                //var user = await _supabase.Client.Auth.GetUser(accessToken);

                var user = session?.User ?? _supabase.Client.Auth.CurrentSession?.User;

                if (user == null)
                    throw new Exception("User returned null");

                SetUser(user);

                // Notify listeners (e.g. TripListViewModel) that a session was restored,
                // so data gets loaded even when no explicit interactive login occurred.
                WeakReferenceMessenger.Default.Send(new LoginCompletedMessage());
            }
            catch (Exception ex)
            {
                // Refresh się nie udał → usuwamy stary token
                SecureStorage.Remove("access_token");
                SecureStorage.Remove("refresh_token");
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

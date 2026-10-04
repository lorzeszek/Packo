using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Interfaces;
using Packo.Messages;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;

namespace Packo.ViewModels
{
    public partial class ShellHeaderViewModel : BaseViewModel
    {
        //private readonly ISessionService _sessionService;
        //private readonly IGoogleAuthService _googleAuthService;
        //private readonly ISupabaseService _supabaseService;

        //public ISessionService Session => _sessionService;

        //public IRelayCommand LoginWithGoogleCommand => new AsyncRelayCommand(LoginWithGoogle);
        public IAsyncRelayCommand LoginWithGoogleCommand { get; }

        public IRelayCommand LogoutCommand => new AsyncRelayCommand(Logout);

        public ShellHeaderViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {

            LoginWithGoogleCommand = new AsyncRelayCommand(LoginWithGoogle, () => !Session.GlobalIsBusy);

        }
        //public ShellHeaderViewModel(ISessionService sessionService, IGoogleAuthService googleAuthService, ISupabaseService supabaseService)
        //{
        //    _sessionService = sessionService;
        //    _googleAuthService = googleAuthService;
        //    _supabaseService = supabaseService;
        //}

        //private void SetBusy(bool isBusy)
        //{
        //    MainThread.BeginInvokeOnMainThread(() =>
        //    {
        //        Session.GlobalIsBusy = isBusy;
        //        LoginWithGoogleCommand.NotifyCanExecuteChanged();
        //    });
        //}

        private async Task LoginWithGoogle()
        {
            if (Session.GlobalIsBusy)
                return;

            try
            {
                SetBusy(true, LoginWithGoogleCommand);

                var token = await _googleAuthService.SignInWithGoogleAsync();
                if (string.IsNullOrWhiteSpace(token))
                    return;

                var authSession = await _supabase.Client.Auth.SignInWithIdToken(Supabase.Gotrue.Constants.Provider.Google, token);
                if (authSession is null)
                    return;

                Session.SetUser(authSession.User);

                await _tripRepository.StartRealtimeAsync();
                await _packingItemRepository.StartRealtimeAsync();

                // Sync trips first (this now persists RemoteTripId into local storage)
                await _tripRepository.SyncPendingChangesAsync();

                // Now packing items can be synced once (their pending rows should already contain RemoteTripId)
                await _packingItemRepository.SyncPendingChangesAsync();

                WeakReferenceMessenger.Default.Send(new LoginCompletedMessage());
                await Shell.Current.GoToAsync("//TripList");
            }
            catch (Exception ex)
            {
                // obsługa błędu
            }
            finally
            {
                SetBusy(false, LoginWithGoogleCommand);
            }
        }

        private async Task Logout()
        {
            try
            {
                var popup = new ConfirmPopup("Log out", "Do you want to log out?");

                var result = await Application.Current.MainPage.ShowPopupAsync<bool>(popup);

                if (result.Result)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Session.GlobalIsBusy = true;
                        LoginWithGoogleCommand.NotifyCanExecuteChanged();
                    });

                    await _tripRepository.UnsubscribeFromTripChangesAsync();
                    await _packingItemRepository.UnsubscribeFromPackingItemChangesAsync();
                    await _supabase.Client.Auth.SignOut();
                    await _tripRepository.DeleteLocalTrips();

                    WeakReferenceMessenger.Default.Send(new LogoutCompletedMessage());
                    await Shell.Current.GoToAsync("//Home");
                }
            }
            catch (Exception ex)
            {
                // Handle error
            }
            finally
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Session.GlobalIsBusy = false;
                    LoginWithGoogleCommand.NotifyCanExecuteChanged();
                });
            }
        }
    }
}

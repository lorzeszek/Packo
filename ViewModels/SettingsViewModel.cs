using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Popups;
using Packo.Helpers;
using Packo.Services;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;

namespace Packo.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    private bool _refreshingThemeOptions;
    private readonly TripReminderService _reminders;
    private readonly ITripListPreloadService _tripPreload;
    private readonly IWeatherPreloadService _weatherPreload;
    private readonly IDocumentPreloadService _documentPreload;

    [ObservableProperty]
    private string? deleteAccountError;

    public IReadOnlyList<string> LanguageOptions => LocalizationResourceManager.Instance.Languages;

    [ObservableProperty]
    private IReadOnlyList<string> themeOptions = [];

    [ObservableProperty]
    private string selectedLanguage = "English";

    [ObservableProperty]
    private string? selectedTheme;

    public SettingsViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService,
        TripReminderService reminders, ITripListPreloadService tripPreload, IWeatherPreloadService weatherPreload, IDocumentPreloadService documentPreload) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
    {
        (_reminders, _tripPreload, _weatherPreload, _documentPreload) = (reminders, tripPreload, weatherPreload, documentPreload);
        RefreshThemeOptions();
        AppThemeManager.ApplySavedTheme();
        SelectedLanguage = LocalizationResourceManager.Instance.CurrentLanguage == "pl" ? "Polski" : "English";
        LocalizationResourceManager.Instance.PropertyChanged += OnLanguageChanged;
    }

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (Session.UserId is not { } account || Session.GlobalIsBusy)
            return;
        var strings = LocalizationResourceManager.Instance;
        DeleteAccountError = null;
        if (!await ConfirmPopup.AskAsync(strings["DeleteAccountConfirmTitle"], strings["DeleteAccountConfirmMessage"],
            strings["DeleteAccount"], strings["Cancel"]))
            return;
        if (Session.UserId != account || Session.GlobalIsBusy)
            return;
        if (!Session.IsAuthenticated || Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            DeleteAccountError = strings["DeleteAccountConnectionRequired"];
            return;
        }

        var deleted = false;
        try
        {
            Session.GlobalIsBusy = true;
            await _tripRepository.UnsubscribeFromTripChangesAsync();
            await _packingItemRepository.UnsubscribeFromPackingItemChangesAsync();
            try { await Session.DeleteAccountAsync(); }
            catch when (Session.UserId != account)
            {
                // The server committed but device credential storage reported an error.
                DeleteAccountError = strings["DeleteAccountLocalCleanupFailed"];
            }
            deleted = true;
            // Wait for in-flight reminder sync to finish after changing the account,
            // then cancel alarms before removing its cached rows and pending uploads.
            try { await _reminders.ResetAfterLogoutAsync(); }
            finally { await _localUserService.DeleteAccountDataAsync(account); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Account deletion: {ex.GetType().Name}");
            DeleteAccountError = strings[deleted ? "DeleteAccountLocalCleanupFailed" : "DeleteAccountFailed"];
            if (!deleted && Session.IsAuthenticated)
            {
                try
                {
                    await _tripRepository.StartRealtimeAsync();
                    await _packingItemRepository.StartRealtimeAsync();
                }
                catch (Exception recoveryError) { System.Diagnostics.Debug.WriteLine(recoveryError.GetType().Name); }
            }
        }
        finally
        {
            Session.GlobalIsBusy = false;
        }

        if (deleted)
        {
            _tripPreload.Invalidate();
            _weatherPreload.Invalidate();
            _documentPreload.Invalidate();
            WeakReferenceMessenger.Default.Send(new LogoutCompletedMessage());
            await Shell.Current.GoToAsync("//TripList");
            if (DeleteAccountError is not null)
                await ConfirmPopup.ShowMessageAsync(strings["DeleteAccount"], DeleteAccountError);
        }
    }

    partial void OnSelectedThemeChanged(string? value)
    {
        if (_refreshingThemeOptions || value is null)
            return;

        var theme = GetThemeKey(value);
        AppThemeManager.SaveTheme(theme);
        AppThemeManager.ApplyTheme(theme);
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        LocalizationResourceManager.Instance.SetLanguage(value);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
            RefreshThemeOptions();
    }

    private void RefreshThemeOptions()
    {
        var savedTheme = AppThemeManager.GetSavedTheme();

        // Updating the Picker's items can send a selection back through the binding.
        _refreshingThemeOptions = true;
        try
        {
            ThemeOptions = [GetThemeDisplay("System"), GetThemeDisplay("Light"), GetThemeDisplay("Dark")];
            SelectedTheme = GetThemeDisplay(savedTheme);
        }
        finally
        {
            _refreshingThemeOptions = false;
        }
    }

    private static string GetThemeDisplay(string theme) => LocalizationResourceManager.Instance[theme switch
    {
        "Light" => "LightTheme",
        "Dark" => "DarkTheme",
        _ => "SystemTheme"
    }];

    private static string GetThemeKey(string display) => display switch
    {
        var value when value == LocalizationResourceManager.Instance["LightTheme"] => "Light",
        var value when value == LocalizationResourceManager.Instance["DarkTheme"] => "Dark",
        _ => "System"
    };
}

using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Helpers;
using Packo.Services;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;

namespace Packo.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    private bool _refreshingThemeOptions;

    public IReadOnlyList<string> LanguageOptions => LocalizationResourceManager.Instance.Languages;

    [ObservableProperty]
    private IReadOnlyList<string> themeOptions = [];

    [ObservableProperty]
    private string selectedLanguage = "English";

    [ObservableProperty]
    private string? selectedTheme;

    public SettingsViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
    {
        RefreshThemeOptions();
        AppThemeManager.ApplySavedTheme();
        SelectedLanguage = LocalizationResourceManager.Instance.CurrentLanguage == "pl" ? "Polski" : "English";
        LocalizationResourceManager.Instance.PropertyChanged += OnLanguageChanged;
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

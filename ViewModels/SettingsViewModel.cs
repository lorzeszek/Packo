using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Helpers;
using Packo.Services;
using System.Collections.ObjectModel;
using Packo.Interfaces;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;

namespace Packo.ViewModels;

public partial class SettingsViewModel : BaseViewModel
{
    public ObservableCollection<string> ThemeOptions { get; } = new();
    public IReadOnlyList<string> LanguageOptions => LocalizationResourceManager.Instance.Languages;

    [ObservableProperty]
    private string selectedLanguage = "English";

    [ObservableProperty]
    private string selectedTheme = "System";

    public SettingsViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
    {
        RefreshThemeOptions();
        SelectedTheme = GetThemeDisplay(AppThemeManager.GetSavedTheme());
        AppThemeManager.ApplyTheme(AppThemeManager.GetSavedTheme());
        SelectedLanguage = LocalizationResourceManager.Instance.CurrentLanguage == "pl" ? "Polski" : "English";
        LocalizationResourceManager.Instance.PropertyChanged += OnLanguageChanged;
    }

    partial void OnSelectedThemeChanged(string value)
    {
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
        RefreshThemeOptions();
        SelectedTheme = GetThemeDisplay(AppThemeManager.GetSavedTheme());
        OnPropertyChanged(nameof(LanguageOptions));
    }

    private void RefreshThemeOptions()
    {
        ThemeOptions.Clear();
        ThemeOptions.Add(LocalizationResourceManager.Instance["SystemTheme"]);
        ThemeOptions.Add(LocalizationResourceManager.Instance["LightTheme"]);
        ThemeOptions.Add(LocalizationResourceManager.Instance["DarkTheme"]);
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

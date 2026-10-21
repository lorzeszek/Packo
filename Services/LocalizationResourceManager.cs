using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Packo.Services;

/// <summary>Looks up app strings and notifies XAML bindings when the language changes.</summary>
public sealed class LocalizationResourceManager : INotifyPropertyChanged
{
    private const string LanguagePreferenceKey = "app_language";
    private const string DefaultLanguage = "en";
    private readonly ResourceManager _resources = new("Packo.Resources.Strings.AppResources", typeof(LocalizationResourceManager).Assembly);
    private CultureInfo _culture = CultureInfo.GetCultureInfo(DefaultLanguage);

    public static LocalizationResourceManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _resources.GetString(key, _culture) ?? $"!{key}!";

    public string CurrentLanguage => Preferences.Get(LanguagePreferenceKey, DefaultLanguage);

    public IReadOnlyList<string> Languages { get; } = ["English", "Polski"];

    private LocalizationResourceManager() { }

    public void ApplySavedLanguage() => SetLanguage(CurrentLanguage, save: false);

    public void SetLanguage(string language, bool save = true)
    {
        var cultureName = language.Equals("pl", StringComparison.OrdinalIgnoreCase)
            || language.Equals("Polski", StringComparison.OrdinalIgnoreCase) ? "pl" : "en";
        var culture = CultureInfo.GetCultureInfo(cultureName);
        _culture = culture;

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (save)
            Preferences.Set(LanguagePreferenceKey, cultureName);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Indexer bindings listen for Item[]; the empty name also refreshes
            // bindings that were materialized by Shell or a cached page.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        });
    }
}

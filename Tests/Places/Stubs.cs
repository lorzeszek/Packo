using System.ComponentModel;

namespace Packo.Services.Interfaces
{
    public interface ISessionService
    {
        string? LocalUserId { get; }
        bool GlobalIsBusy { get; }
        bool IsAuthenticated => true;
    }
}

namespace Packo.Services
{
    public sealed class LocalizationResourceManager : INotifyPropertyChanged
    {
        public static LocalizationResourceManager Instance { get; } = new();
        public event PropertyChangedEventHandler? PropertyChanged;
        public string CurrentLanguage { get; private set; } = "pl";
        public string this[string key] => key == "PlacesSavedCount" ? "Zapisane · {0}" : key;
        public void SetLanguage(string language)
        {
            CurrentLanguage = language;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
}

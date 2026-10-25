using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Services;

namespace Packo.Models
{
    public partial class TransportOptionItem : ObservableObject
    {
        private readonly string _resourceKey;
        public string Name => LocalizationResourceManager.Instance[_resourceKey];

        public bool MatchesStoredName(string name)
        {
            var resources = new System.Resources.ResourceManager(
                "Packo.Resources.Strings.AppResources", typeof(TransportOptionItem).Assembly);
            return new[] { "en", "pl" }.Any(language => string.Equals(name,
                resources.GetString(_resourceKey, System.Globalization.CultureInfo.GetCultureInfo(language)),
                StringComparison.OrdinalIgnoreCase));
        }

        [ObservableProperty]
        private bool isSelected;

        public TransportOptionItem(string name)
        {
            _resourceKey = name;
            LocalizationResourceManager.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Name));
        }
    }
}

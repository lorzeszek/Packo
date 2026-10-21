using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Services;

namespace Packo.Models
{
    public partial class TransportOptionItem : ObservableObject
    {
        private readonly string _resourceKey;
        public string Name => LocalizationResourceManager.Instance[_resourceKey];

        [ObservableProperty]
        private bool isSelected;

        public TransportOptionItem(string name)
        {
            _resourceKey = name;
            LocalizationResourceManager.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Name));
        }
    }
}

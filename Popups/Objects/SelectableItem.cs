using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Packo.Popups.Objects
{
    public class SelectableItem : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private bool _isSelected;
        private int _quantity = 1;

        public int Quantity
        {
            get => _quantity;
            set { _quantity = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

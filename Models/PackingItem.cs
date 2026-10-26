using System.ComponentModel;

namespace Packo.Models
{
    public class PackingItem : INotifyPropertyChanged
    {
        public int? RemoteId { get; set; }
        public int LocalId { get; set; }
        public int LocalTripId { get; set; }
        public int? RemoteTripId { get; set; }
        public string LocalUserId { get; set; }
        public string RemoteUserId { get; set; }
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                OnPropertyChanged(nameof(Name));
            }
        }
        private int _quantity = 1;
        public int Quantity
        {
            get => _quantity;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
                if (_quantity == value) return;
                _quantity = value;
                OnPropertyChanged(nameof(Quantity));
            }
        }
        public DateTime CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string User_id { get; set; } = string.Empty;
        private bool _isPacked;
        public bool IsPacked
        {
            get => _isPacked;
            set
            {
                if (_isPacked != value)
                {
                    _isPacked = value;
                    OnPropertyChanged(nameof(IsPacked));
                }
            }
        }

        public int Category { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

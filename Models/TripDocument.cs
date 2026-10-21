using System.ComponentModel;

using System.Globalization;
using Packo.Services;

namespace Packo.Models
{
    public class TripDocument : INotifyPropertyChanged
    {
        public Guid Id { get; set; }
        public Guid TripId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; }
        public string AddedAtDisplay => string.Format(CultureInfo.CurrentUICulture, LocalizationResourceManager.Instance["AddedAt"], AddedAt);
        public long FileSize { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public TripDocument()
        {
            LocalizationResourceManager.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(AddedAtDisplay));
        }
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

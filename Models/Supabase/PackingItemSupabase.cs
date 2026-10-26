using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.ComponentModel;

namespace Packo.Models.Supabase
{
    [Table("PackingItem")]
    public class PackingItemSupabase : BaseModel, INotifyPropertyChanged
    {
        [PrimaryKey("Id", false)]
        public int Id { get; set; }

        [Column("TripId")]
        public int TripId { get; set; }

        [Column("Name")]
        public string Name { get; set; } = string.Empty;

        [Column("Quantity")]
        public int Quantity { get; set; } = 1;

        [Column("CreatedDate")]
        public DateTime CreatedDate { get; set; }

        [Column("user_id")]
        public string User_id { get; set; } = string.Empty;

        private bool _isPacked;
        [Column("IsPacked")]
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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

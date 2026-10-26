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

        [Column("ModifiedDate")]
        public DateTime? ModifiedDate { get; set; }

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

        [Column("Category")]
        public int Category { get; set; }

        //public Guid Id { get; set; } = Guid.NewGuid();
        //public required string Name { get; set; }
        //public Category Category { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

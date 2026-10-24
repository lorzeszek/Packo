using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.ComponentModel;
using Newtonsoft.Json.Linq;

namespace Packo.Models.Supabase
{
    [Table("Trip")]
    public class TripSupabase : BaseModel, INotifyPropertyChanged
    {
        [PrimaryKey("Id", false)]
        public int Id { get; set; }

        [Column("ClientId")]
        //public Guid ClientId { get; set; }
        public string ClientId { get; set; } = string.Empty;

        [Column("Destination")]
        public string Destination { get; set; } = string.Empty;

        [Column("CoverTheme")]
        public string? CoverTheme { get; set; }

        [Column("CreatedDate")]
        public DateTime CreatedDate { get; set; }

        [Column("ModifiedDate")]
        public DateTime? ModifiedDate { get; set; }

        [Column("StartDate")]
        public DateTime? StartDate { get; set; }

        [Column("EndDate")]
        public DateTime? EndDate { get; set; }

        [Column("AdultsCount")]
        public int AdultsCount { get; set; } = 1;

        [Column("ChildrenAges")]
        public JToken? ChildrenAges { get; set; }

        [Column("SelectedTransportTypes")]
        public JToken? SelectedTransportTypes { get; set; }

        [Column("TripType")]
        public string TripType { get; set; } = "Leisure";

        [Column("user_id")]
        public string User_id { get; set; } = string.Empty;

        [Column("LocalTripId")]
        public int LocalTripId { get; set; }

        //[Column("IsInTrash")]
        //public bool IsInTrash { get; set; }



        private bool _isInTrash;
        [Column("IsInTrash")]
        public bool IsInTrash
        {
            get => _isInTrash;
            set
            {
                if (_isInTrash != value)
                {
                    _isInTrash = value;
                    OnPropertyChanged(nameof(IsInTrash));
                }
            }
        }

        private bool _isActive;
        [Column("IsActive")]
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }



        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    //[Table("Trip")]
    //public partial class Trip : BaseModel, INotifyPropertyChanged
    //{
    //    [PrimaryKey("Id", false)]
    //    public int Id { get; set; }

    //    [Column("Destination")]
    //    public string Destination { get; set; } = string.Empty;

    //    [Column("CreatedDate")]
    //    public DateTime CreatedDate { get; set; }

    //    [Column("ModifiedDate")]
    //    public DateTime? ModifiedDate { get; set; }

    //    [Column("StartDate")]
    //    public DateTime? StartDate { get; set; }

    //    [Column("EndDate")]
    //    public DateTime? EndDate { get; set; }

    //    [Column("user_id")]
    //    public string User_id { get; set; } = string.Empty;

    //    private bool _isInTrash;
    //    [Column("IsInTrash")]
    //    public bool IsInTrash
    //    {
    //        get => _isInTrash;
    //        set
    //        {
    //            if (_isInTrash != value)
    //            {
    //                _isInTrash = value;
    //                OnPropertyChanged(nameof(IsInTrash));
    //            }
    //        }
    //    }

    //    private bool _isActive;
    //    [Column("IsActive")]
    //    public bool IsActive
    //    {
    //        get => _isActive;
    //        set
    //        {
    //            if (_isActive != value)
    //            {
    //                _isActive = value;
    //                OnPropertyChanged(nameof(IsActive));
    //            }
    //        }
    //    }

    //    //[Reference(typeof(PackingItem), ReferenceAttribute.JoinType.Left)]
    //    //public List<PackingItem> Items { get; set; } = new();


    //    public event PropertyChangedEventHandler? PropertyChanged;

    //    protected virtual void OnPropertyChanged(string propertyName)
    //    {
    //        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    //    }
    //}
}

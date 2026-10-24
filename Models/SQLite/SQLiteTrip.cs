using Packo.Models.Enums;
using SQLite;

namespace Packo.Models.SQLite
{
    [Table("SQLiteTrip")]
    public class SQLiteTrip
    {
        [PrimaryKey, AutoIncrement]
        public int LocalTripId { get; set; }
        public int? RemoteTripId { get; set; }
        public string LocalUserId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Destination { get; set; } = string.Empty;
        public int AdultsCount { get; set; } = 1;
        public string ChildrenAgesJson { get; set; } = "[]";
        public string SelectedTransportTypesJson { get; set; } = "[]";
        public string TripType { get; set; } = "Leisure";
        public bool IsActive { get; set; }
        public bool IsInTrash { get; set; }
        public CoverThemeType CoverTheme { get; set; }
        public string CoverImagePath { get; set; }
    }
}

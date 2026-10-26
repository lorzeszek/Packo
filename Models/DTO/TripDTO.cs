using Packo.Models.Enums;

namespace Packo.Models.DTO
{
    public class TripDTO
    {
        public int RemoteTripId { get; set; }
        public int LocalTripId { get; set; }
        public string LocalUserId { get; set; }
        public string RemoteUserId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Destination { get; set; } = string.Empty;
        public int AdultsCount { get; set; } = 1;
        public List<int> ChildrenAges { get; set; } = [];
        public List<string> SelectedTransportTypes { get; set; } = [];
        public TripType TripType { get; set; } = TripType.Leisure;
        public bool IsActive { get; set; }
        public bool IsInTrash { get; set; }
        public CoverThemeType CoverTheme { get; set; }
        public string CoverImagePath { get; set; }
    }
}

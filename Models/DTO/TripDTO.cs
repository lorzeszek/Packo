using Packo.Models.Enums;

namespace Packo.Models.DTO
{
    public class TripDTO
    {
        public int RemoteTripId { get; set; }
        public int LocalTripId { get; set; }
        public string LocalUserId { get; set; }
        public string RemoteUserId { get; set; }
        public bool IsJoinedTrip { get; set; }
        public List<TripParticipant> Participants { get; set; } = [];
        public DateTime CreatedDate { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? TimeZoneId { get; set; }
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

    public sealed record TripParticipant(int TripId, string UserId, bool IsOwner, string Name, string AvatarUrl)
    {
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[0].ToString().ToUpperInvariant();
        public Uri? Avatar => Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host == "googleusercontent.com" || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            ? uri : null;
        public bool HasAvatar => Avatar is not null;
    }
}

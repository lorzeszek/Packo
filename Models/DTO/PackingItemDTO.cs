using CommunityToolkit.Mvvm.ComponentModel;

namespace Packo.Models.DTO
{
    public class PackingItemDTO : ObservableObject
    {
        public int? RemotePackingItemId { get; set; }
        public int LocalPackingItemId { get; set; }
        public int LocalTripId { get; set; }
        public int? RemoteTripId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public int Category { get; set; }
        public string LocalUserId { get; set; }
        public string RemoteUserId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public bool IsPacked { get; set; }
        string PackingItemJson { get; set; }
    }
}

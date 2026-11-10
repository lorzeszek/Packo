using CommunityToolkit.Mvvm.ComponentModel;

namespace Packo.Models.DTO
{
    public class PackingItemDTO : ObservableObject
    {
        public int? RemotePackingItemId { get; set; }
        public int LocalPackingItemId { get; set; }
        public int LocalTripId { get; set; }
        public int? RemoteTripId { get; set; }
        public Guid PackingListId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public string LocalUserId { get; set; }
        public string RemoteUserId { get; set; }
        public DateTime CreatedDate { get; set; }
        public bool IsPacked { get; set; }
        public long NameVersion { get; set; }
        public long QuantityVersion { get; set; }
        public long IsPackedVersion { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public int? PreviousQuantity { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool? PreviousIsPacked { get; set; }
        string PackingItemJson { get; set; }
    }
}

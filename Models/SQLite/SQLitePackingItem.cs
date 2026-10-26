using SQLite;

namespace Packo.Models.SQLite
{
    [Table("SQLitePackingItem")]
    public class SQLitePackingItem
    {
        [PrimaryKey, AutoIncrement]
        public int LocalPackingItemId { get; set; }
        public int? RemotePackingItemId { get; set; }
        public int LocalTripId { get; set; }
        public int? RemoteTripId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public string LocalUserId { get; set; }
        public string RemoteUserId { get; set; }
        public DateTime CreatedDate { get; set; }
        public bool IsPacked { get; set; }
        // Preserve obsolete cache rows for recovery without counting them as current items.
        public bool IsStaleCache { get; set; }
    }
}

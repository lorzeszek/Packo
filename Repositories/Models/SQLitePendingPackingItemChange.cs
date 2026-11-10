using SQLite;

namespace Packo.Repositories.Models
{
    public class SQLitePendingPackingItemChange
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public int RemoteTripId { get; set; }
        public int LocalTripId { get; set; }
        public int LocalPackingItemId { get; set; }
        public string LocalUserId { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;  // "Add", "Update", "Delete"
        public string PackingItemJson { get; set; } = string.Empty;   // serializacja Trip do JSON
        public string OperationId { get; set; } = string.Empty;
        public string? ChangesJson { get; set; }
        public string? BeforeJson { get; set; }
        public bool Attempted { get; set; }
        //public DateTime CreatedDate { get; set; } = DateTime.Now;
    }
}

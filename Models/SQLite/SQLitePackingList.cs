using SQLite;

namespace Packo.Models.SQLite;

public enum PackingListOperation
{
    None,
    Create,
    Rename,
    Delete
}

public sealed class SQLitePackingList
{
    [PrimaryKey, AutoIncrement]
    public int RowId { get; set; }
    public Guid Id { get; set; }
    [Indexed]
    public string LocalUserId { get; set; } = string.Empty;
    public int LocalTripId { get; set; }
    public int RemoteTripId { get; set; }
    public string? Name { get; set; }
    public PackingListOperation Operation { get; set; }
}

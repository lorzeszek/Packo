using SQLite;

namespace Packo.Models.SQLite;

[Table("SQLiteTripPlaces")]
public sealed class SQLiteTripPlaces
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    [Indexed]
    public string LocalUserId { get; set; } = string.Empty;
    public int LocalTripId { get; set; }
    public int RemoteTripId { get; set; }
    public string StateJson { get; set; } = string.Empty;
}

using SQLite;

namespace Packo.Models.SQLite;

public sealed class SQLiteTripReminder
{
    [PrimaryKey] public string Key { get; set; } = "";
    [Indexed] public string LocalUserId { get; set; } = "";
    public string ReminderId { get; set; } = "";
    public int LocalTripId { get; set; }
    public int RemoteTripId { get; set; }
    public string DefinitionJson { get; set; } = "";
    public string TripJson { get; set; } = "";
    public bool Dirty { get; set; }
    public long ExpectedVersion { get; set; }
    public string OperationId { get; set; } = "";
    public string? SyncError { get; set; }
}

public sealed class SQLiteReminderPreference
{
    [PrimaryKey] public string Key { get; set; } = "";
    [Indexed] public string LocalUserId { get; set; } = "";
    public int LocalTripId { get; set; }
    public int RemoteTripId { get; set; }
    public string ReminderId { get; set; } = "*";
    public bool Muted { get; set; }
    public bool Dirty { get; set; }
}

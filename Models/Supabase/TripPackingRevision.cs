using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Packo.Models.Supabase;

[Table("TripPackingRevision")]
public sealed class TripPackingRevision : BaseModel
{
    [PrimaryKey("Id", false)]
    public Guid Id { get; set; }

    [Column("TripId")]
    public int TripId { get; set; }
}

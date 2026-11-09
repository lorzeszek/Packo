using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace Packo.Models.Supabase;

[Table("PackingList")]
public sealed class PackingListSupabase : BaseModel
{
    [PrimaryKey("TripId", true)]
    public int TripId { get; set; }

    [PrimaryKey("Id", true)]
    public Guid Id { get; set; }

    [Column("Name")]
    public string? Name { get; set; }
}

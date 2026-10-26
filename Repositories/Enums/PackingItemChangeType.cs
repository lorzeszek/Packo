using Packo.Models.DTO;

namespace Packo.Repositories.Enums
{
    public enum PackingItemChangeType
    {
        Insert,
        Update,
        Delete,
        IdentityAssigned
    }

    public record PackingItemChange(
    PackingItemChangeType Type,
    PackingItemDTO Item
    );
}

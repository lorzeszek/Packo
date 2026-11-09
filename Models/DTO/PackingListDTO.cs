namespace Packo.Models.DTO;

public sealed record PackingListDTO(Guid Id, string? Name, int Packed = 0, int Total = 0)
{
    public const int MaxNameLength = 100;
    public double Progress => Total == 0 ? 0 : (double)Packed / Total;
}

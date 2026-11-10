namespace Packo.Repositories.Interfaces;

public sealed record PackingOperationResult(string Status, int? ItemId);

public interface IPackingOperationRemote
{
    Task<PackingOperationResult> ApplyOperationAsync(object parameters);
}

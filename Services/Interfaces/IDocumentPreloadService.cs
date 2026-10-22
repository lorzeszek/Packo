using Packo.Models.SQLite;

namespace Packo.Services.Interfaces
{
    public interface IDocumentPreloadService
    {
        Task PrefetchAsync();
        Task<IReadOnlyList<SQLiteTripDocument>> GetDocumentsAsync(bool forceRefresh = false);
        void Invalidate();
    }
}

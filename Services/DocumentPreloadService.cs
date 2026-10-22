using Packo.Models.SQLite;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;

namespace Packo.Services
{
    public class DocumentPreloadService : IDocumentPreloadService
    {
        private readonly ITripRepository _tripRepository;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<IReadOnlyList<SQLiteTripDocument>>? _cachedRequest;

        public DocumentPreloadService(ITripRepository tripRepository)
        {
            _tripRepository = tripRepository;
        }

        public async Task PrefetchAsync()
        {
            try
            {
                await GetDocumentsAsync();
            }
            catch
            {
                // The Documents tab will retry if local storage is temporarily unavailable.
            }
        }

        public async Task<IReadOnlyList<SQLiteTripDocument>> GetDocumentsAsync(bool forceRefresh = false)
        {
            Task<IReadOnlyList<SQLiteTripDocument>> request;

            await _gate.WaitAsync();
            try
            {
                if (forceRefresh || _cachedRequest is null)
                    _cachedRequest = LoadDocumentsAsync();

                request = _cachedRequest;
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                return await request;
            }
            catch
            {
                await _gate.WaitAsync();
                try
                {
                    if (ReferenceEquals(_cachedRequest, request))
                        _cachedRequest = null;
                }
                finally
                {
                    _gate.Release();
                }

                throw;
            }
        }

        public void Invalidate() => _cachedRequest = null;

        private async Task<IReadOnlyList<SQLiteTripDocument>> LoadDocumentsAsync() =>
            await _tripRepository.GetByTripDocsAsync();
    }
}

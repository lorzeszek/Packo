using Packo.Models.SQLite;

namespace Packo.Services.Interfaces
{
    public interface ILocalUserService
    {
        //Task<SQLiteUser> GetOrCreateAsync();
        Task<SQLiteUser> GetOrCreateLocalUserAsync();
        Task<SQLiteUser> CreateLocalUserAsync();
        Task<SQLiteUser> GetOrCreateAccountAsync(string supabaseUserId, bool attachGuestDrafts = false);
    }
}

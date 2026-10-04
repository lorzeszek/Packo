using Packo.Models.SQLite;

namespace Packo.Services.Interfaces
{
    public interface ILocalUserService
    {
        //Task<SQLiteUser> GetOrCreateAsync();
        Task<SQLiteUser> GetOrCreateLocalUserAsync();
        Task<SQLiteUser> CreateLocalUserAsync();
        Task LinkSupabaseUserAsync(string supabaseUserId);
    }
}

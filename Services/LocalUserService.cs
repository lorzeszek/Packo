using Packo.Models.SQLite;
using Packo.Services.Interfaces;
using SQLite;

namespace Packo.Services
{
    public class LocalUserService : ILocalUserService
    {
        private readonly SQLiteAsyncConnection _db;

        public LocalUserService(SQLiteAsyncConnection db)
        {
            _db = db;
        }

        public async Task<SQLiteUser> CreateLocalUserAsync()
        {
            var user = new SQLiteUser
            {
                LocalUserId = Guid.NewGuid().ToString(),
                CreatedDate = DateTime.Now
            };

            await _db.InsertAsync(user);
            return user;
        }

        public async Task<SQLiteUser> GetOrCreateLocalUserAsync()
        {
            return await _db.Table<SQLiteUser>().FirstOrDefaultAsync() ?? await CreateLocalUserAsync();
        }

        //public async Task<SQLiteUser> GetOrCreateAsync()
        //{
        //    var user = await _db.Table<SQLiteUser>().FirstOrDefaultAsync();

        //    if (user != null)
        //        return user;

        //    user = new SQLiteUser
        //    {
        //        LocalUserId = Guid.NewGuid().ToString(),
        //        CreatedDate = DateTime.UtcNow
        //    };

        //    await _db.InsertAsync(user);
        //    return user;
        //}

        public async Task LinkSupabaseUserAsync(string supabaseUserId)
        {
            var user = await _db.Table<SQLiteUser>().FirstAsync();
            user.SupabaseUserId = supabaseUserId;
            await _db.UpdateAsync(user);
        }
    }
}
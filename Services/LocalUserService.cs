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
            SQLiteUser? user = null;
            await _db.RunInTransactionAsync(db =>
            {
                user = db.Table<SQLiteUser>().FirstOrDefault(x => x.SupabaseUserId == null);
                if (user is not null)
                    return;
                user = new SQLiteUser { LocalUserId = Guid.NewGuid().ToString(), CreatedDate = DateTime.Now };
                db.Insert(user);
            });
            return user!;
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

        public async Task<SQLiteUser> GetOrCreateAccountAsync(string supabaseUserId, bool attachGuestDrafts = false)
        {
            SQLiteUser? user = null;
            await _db.RunInTransactionAsync(db =>
            {
                user = db.Table<SQLiteUser>().FirstOrDefault(x => x.SupabaseUserId == supabaseUserId);
                if (user is not null)
                {
                    var guest = attachGuestDrafts ? db.Table<SQLiteUser>().FirstOrDefault(x => x.SupabaseUserId == null) : null;
                    if (guest is not null)
                    {
                        const string draftTrips = "SELECT LocalTripId FROM SQLiteTrip WHERE LocalUserId = ? AND coalesce(RemoteTripId, 0) = 0";
                        foreach (var table in new[] { "SQLitePackingItem", "SQLitePackingList", "SQLitePendingPackingItemChange" })
                            db.Execute($"UPDATE {table} SET LocalUserId = ? WHERE LocalUserId = ? AND LocalTripId IN ({draftTrips})",
                                user.LocalUserId, guest.LocalUserId, guest.LocalUserId);
                        db.Execute("UPDATE SQLitePendingTripChange SET LocalUserId = ? WHERE LocalUserId = ?",
                            user.LocalUserId, guest.LocalUserId);
                        db.Execute("UPDATE SQLiteTrip SET LocalUserId = ? WHERE LocalUserId = ? AND coalesce(RemoteTripId, 0) = 0",
                            user.LocalUserId, guest.LocalUserId);
                    }
                    return;
                }
                user = db.Table<SQLiteUser>().FirstOrDefault(x => x.SupabaseUserId == null);
                if (user is not null)
                {
                    user.SupabaseUserId = supabaseUserId;
                    db.Update(user);
                }
                else
                {
                    user = new SQLiteUser { LocalUserId = Guid.NewGuid().ToString(),
                        SupabaseUserId = supabaseUserId, CreatedDate = DateTime.Now };
                    db.Insert(user);
                }
            });
            return user!;
        }
    }
}

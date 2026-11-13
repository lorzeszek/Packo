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
                        if (db.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'SQLiteTripReminder'") > 0)
                        {
                            var draftIds = db.Query<Packo.Models.SQLite.SQLiteTrip>(
                                "SELECT * FROM SQLiteTrip WHERE LocalUserId = ? AND coalesce(RemoteTripId, 0) = 0", guest.LocalUserId)
                                .Select(x => x.LocalTripId).ToHashSet();
                            foreach (var reminder in db.Table<Packo.Models.SQLite.SQLiteTripReminder>()
                                .Where(x => x.LocalUserId == guest.LocalUserId).ToList().Where(x => draftIds.Contains(x.LocalTripId)))
                            {
                                var oldKey = reminder.Key;
                                reminder.LocalUserId = user.LocalUserId;
                                reminder.Key = user.LocalUserId + ":" + reminder.ReminderId;
                                db.InsertOrReplace(reminder);
                                db.Delete<Packo.Models.SQLite.SQLiteTripReminder>(oldKey);
                            }
                            foreach (var pref in db.Table<Packo.Models.SQLite.SQLiteReminderPreference>()
                                .Where(x => x.LocalUserId == guest.LocalUserId).ToList().Where(x => draftIds.Contains(x.LocalTripId)))
                            {
                                var oldKey = pref.Key;
                                pref.Key = user.LocalUserId + oldKey[guest.LocalUserId.Length..];
                                pref.LocalUserId = user.LocalUserId;
                                db.InsertOrReplace(pref);
                                db.Delete<Packo.Models.SQLite.SQLiteReminderPreference>(oldKey);
                            }
                        }
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

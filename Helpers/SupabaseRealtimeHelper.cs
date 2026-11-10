using Supabase.Postgrest.Models;

namespace Packo.Helpers
{
    public static class RealtimeSubscriptionHelper
    {
        public static async Task<IReadOnlyList<Supabase.Realtime.RealtimeChannel>> SubscribeTableChanges<T>(
            Supabase.Client client,
            Action<T> onInsert,
            Action<T> onUpdate,
            Action<T> onDelete
        ) where T : BaseModel, new()
        {
            var table = client.From<T>();

            var channel = await table.On(
                Supabase.Realtime.PostgresChanges.PostgresChangesOptions.ListenType.All,
                (sender, args) =>
                {
                    switch (args.Payload.Data.Type.ToString())
                    {
                        case "Insert":
                            var inserted = args.Model<T>();
                            if (inserted != null)
                                onInsert?.Invoke(inserted);
                            break;

                        case "Update":
                            var updated = args.Model<T>();
                            if (updated != null)
                                onUpdate?.Invoke(updated);
                            break;

                        case "Delete":
                            var deleted = args.OldModel<T>();
                            if (deleted != null)
                                onDelete?.Invoke(deleted);
                            break;
                    }
                });
            return [channel];
        }
    }
}

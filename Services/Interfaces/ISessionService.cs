using Supabase.Gotrue;

namespace Packo.Services.Interfaces
{
    public interface ISessionService
    {
        string? UserId { get; }
        User? User { get; }
        bool IsAuthenticated { get; }
        string? AvatarUrl { get; }
        bool HasAvatar { get; }
        string UserInitial { get; }
        bool GlobalIsBusy { get; set; }
        bool HasLocalUser { get; }
        string? LocalUserId { get; }
        Task InitializeAsync();
        Task<bool> TryRecoverSessionAsync(bool forceRefresh = false);
        Task SetAuthenticatedUserAsync(User user, bool attachGuestDrafts = false);
        Task SwitchToGuestAsync();
        void SetLocalUser(string localUserId);
    }
}

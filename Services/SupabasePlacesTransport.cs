using System.Net.Http.Json;
using System.Net.Http.Headers;
using Packo.Services.Interfaces;

namespace Packo.Services;

public sealed class SupabasePlacesTransport(HttpClient http, ISupabaseService supabase, ISessionService session) : IPlacesTransport
{
    public async Task<HttpResponseMessage> SendAsync(object body, CancellationToken token)
    {
        var userId = session.UserId;
        if (!session.IsAuthenticated && !await session.TryRecoverSessionAsync().WaitAsync(token))
            throw new PlacesApiException("sign_in_required");
        if (!session.IsAuthenticated || (userId is not null && userId != session.UserId))
            throw new PlacesApiException("sign_in_required");
        using var request = new HttpRequestMessage(HttpMethod.Post, SupabaseService.ProjectUrl + "/functions/v1/trip-places")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("apikey", SupabaseService.PublicAnonKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", supabase.Client.Auth.CurrentSession?.AccessToken
            ?? throw new PlacesApiException("sign_in_required"));
        request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Services.Interfaces;

namespace Packo.Services;

public sealed class PlaceSuggestionService(IPlacesTransport transport) : IPlaceSuggestionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool RequiresSignIn => true;

    public static string ContextKey(TripDTO trip, string language) => "places-v2:" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Destination = trip.Destination.Trim().ToUpperInvariant(), Language = language }))));

    public async Task<IReadOnlyList<TripPlace>> FindAsync(TripDTO trip, string language,
        IReadOnlyCollection<string> excludedIds, CancellationToken token)
    {
        using var document = await PostAsync(new { action = "search", destination = trip.Destination,
            language = language == "pl" ? "pl" : "en", excludedIds = excludedIds.Where(x => x.StartsWith("google:", StringComparison.Ordinal)).Take(200) }, token);
        return document.RootElement.GetProperty("places").Deserialize<List<TripPlace>>(Json)
            ?.Where(x => x.IsGoogle && !string.IsNullOrWhiteSpace(x.Name)).DistinctBy(x => x.Id).Take(8).ToList() ?? [];
    }

    public async Task<TripPlaceDetails> GetDetailsAsync(string placeId, string language, CancellationToken token)
    {
        using var document = await PostAsync(new { action = "details", placeId, language = language == "pl" ? "pl" : "en" }, token);
        return document.RootElement.Deserialize<TripPlaceDetails>(Json) ?? throw new JsonException("Missing place details.");
    }

    public async Task<Uri?> GetWebsiteAsync(string placeId, CancellationToken token)
    {
        using var document = await PostAsync(new { action = "website", placeId }, token);
        return document.RootElement.TryGetProperty("websiteUrl", out var website) ? TripPlace.WebUri(website.GetString()) : null;
    }

    public async Task<byte[]?> GetPhotoAsync(string photoReference, CancellationToken token)
    {
        using var response = await transport.SendAsync(new { action = "photo", photoReference }, token);
        await CheckAsync(response, token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new JsonException("Invalid photo response.");
        var data = await response.Content.ReadAsByteArrayAsync(token);
        if (data.Length > 4 * 1024 * 1024) throw new InvalidDataException("Photo is too large.");
        return data;
    }

    private async Task<JsonDocument> PostAsync(object body, CancellationToken token)
    {
        using var response = await transport.SendAsync(body, token);
        await CheckAsync(response, token);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "sign_in_required", HttpStatusCode.TooManyRequests => "quota_exceeded",
            HttpStatusCode.NotFound => "not_configured", _ => "unavailable"
        };
        try
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (error.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                code = value.GetString() ?? code;
        }
        catch (JsonException) { }
        throw new PlacesApiException(code);
    }
}

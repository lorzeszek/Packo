using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;

internal static class GatewayChecks
{
    public static async Task RunAsync(ITripPlacesRepository repository, TripDTO trip, Action<bool, string> check)
    {
        var transport = new Transport();
        var service = new PlaceSuggestionService(transport);
        var places = await service.FindAsync(trip, "pl", ["Q1", "google:OldPlace"], CancellationToken.None);
        check(places.Single().Id == "google:ChIJMuseum" && service.RequiresSignIn,
            "Google discovery uses the authenticated Supabase gateway and parses real place identifiers");
        var signedOut = new TripPlacesViewModel(repository, service, new SignedOutSession(trip.LocalUserId));
        await signedOut.LoadAsync(trip);
        signedOut.SetOnline(true);
        check(signedOut.NeedsSignIn && !signedOut.FindCommand.CanExecute(null) && signedOut.CanEdit,
            "Guests can edit their offline list but cannot trigger paid Google discovery");
        check(transport.LastBody.GetProperty("excludedIds").GetArrayLength() == 1
            && !transport.LastBody.TryGetProperty("adultsCount", out _),
            "Only the destination, language and Google exclusion IDs are sent to the gateway");
        var googlePlace = places.Single() with { UserLabel = "Sunday walk", UserNote = "Meet at noon" };
        await repository.ReplaceSuggestionsAsync(trip, PlaceSuggestionService.ContextKey(trip, "pl"), [googlePlace]);
        await repository.SaveAsync(trip, googlePlace);
        var cached = await repository.LoadAsync(trip);
        var saved = cached.Saved.Single().Place;
        check(saved.Id == googlePlace.Id && saved.Name == "" && saved.LocationText == "" && saved.Latitude is null
            && saved.WebsiteUrl is null && saved.Attributions.Count == 0 && cached.Suggestions.Single().Name == "",
            "SQLite stores no Google business name, address, coordinates, website or attribution snapshot");
        check(saved.UserLabel == "Sunday walk" && saved.UserNote == "Meet at noon",
            "A user's own labels and notes remain readable offline beside the saved place ID");
        await repository.SetVisitedAsync(trip, googlePlace.Id, true);
        await repository.SaveAsync(trip, saved with { UserNote = "Meet at one" });
        cached = await repository.LoadAsync(trip);
        check(cached.Saved.Single().Visited && cached.Saved.Single().Place.UserNote == "Meet at one",
            "Editing a Google-place note preserves visited status without persisting fetched Google data");
        check(saved.MapUri.Query.Contains("query_place_id=ChIJMuseum"),
            "A saved Google place can open Maps by its exempt identifier without cached coordinates");
        var details = await service.GetDetailsAsync(saved.Id, "pl", CancellationToken.None);
        check(details.PhotoReference == "places/ChIJMuseum/photos/reference" && details.PhotoAttributions!.Single().Name == "Photographer",
            "Photo references and author attributions are loaded only with place details");
        check(await service.GetWebsiteAsync(saved.Id, CancellationToken.None) == new Uri("https://museum.example"),
            "The official website is requested separately, only when the website action is used");
        check((await service.GetPhotoAsync(details.PhotoReference!, CancellationToken.None))!.SequenceEqual(new byte[] { 1, 2, 3 }),
            "Photos are fetched as in-memory bytes, without a key-bearing or disk-cached image URL");
        transport.NextResponse = new(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("{\"code\":\"quota_exceeded\"}") };
        try { await service.FindAsync(trip, "pl", [], CancellationToken.None); throw new Exception("Expected quota rejection"); }
        catch (PlacesApiException ex) { check(ex.ResourceKey == "PlacesQuotaExceeded", "Quota failures have a dedicated localized user message"); }
        foreach (var (code, resource) in new[] {
            ("request_too_soon", "PlacesRequestTooSoon"), ("search_daily_limit", "PlacesSearchDailyLimit"),
            ("details_daily_limit", "PlacesDetailsDailyLimit"), ("photo_daily_limit", "PlacesPhotoDailyLimit"),
            ("website_daily_limit", "PlacesWebsiteDailyLimit"), ("google_rate_limited", "PlacesGoogleRateLimited") })
        {
            transport.NextResponse = new(HttpStatusCode.TooManyRequests)
                { Content = new StringContent(JsonSerializer.Serialize(new { code })) };
            try { await service.FindAsync(trip, "pl", [], CancellationToken.None); throw new Exception("Expected limit rejection"); }
            catch (PlacesApiException ex) { check(ex.ResourceKey == resource, $"{code} has its own localized explanation"); }
        }
        transport.NextResponse = new(HttpStatusCode.OK) { Content = new StringContent("{\"websiteUrl\":\"javascript:alert(1)\"}") };
        check(await service.GetWebsiteAsync(saved.Id, CancellationToken.None) is null, "Invalid website schemes are never launched");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.GetDetailsAsync(saved.Id, "pl", cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(true, "Cancellation reaches the gateway transport"); }
    }

    private sealed class SignedOutSession(string id) : ISessionService
    {
        public string? LocalUserId => id;
        public bool GlobalIsBusy => false;
        public bool IsAuthenticated => false;
    }

    private sealed class Transport : IPlacesTransport
    {
        public JsonElement LastBody;
        public HttpResponseMessage? NextResponse;
        public Task<HttpResponseMessage> SendAsync(object body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastBody = JsonSerializer.SerializeToElement(body);
            if (NextResponse is { } next) { NextResponse = null; return Task.FromResult(next); }
            var place = new TripPlace { Id = "google:ChIJMuseum", Provider = "google", Name = "Museum",
                LocationText = "Restricted Google address", Latitude = 52, Longitude = 21, WebsiteUrl = "https://museum.example",
                Attributions = [new("Provider", "https://provider.example")] };
            var action = LastBody.GetProperty("action").GetString();
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (action == "photo")
            {
                response.Content = new ByteArrayContent([1, 2, 3]);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            }
            else
            {
                object payload = action switch
                {
                    "search" => new { places = new[] { place, place } },
                    "details" => new TripPlaceDetails(place, "places/ChIJMuseum/photos/reference", [new("Photographer", "https://photographer.example")]),
                    "website" => new { websiteUrl = "https://museum.example" },
                    _ => throw new Exception("Unknown action")
                };
                response.Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        }
    }
}

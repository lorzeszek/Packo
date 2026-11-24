namespace Packo.Services;

public sealed class PlacesApiException(string code) : Exception(code)
{
    public string Code { get; } = code;
    public string ResourceKey => Code switch
    {
        "sign_in_required" => "PlacesSignInRequired",
        "quota_exceeded" => "PlacesQuotaExceeded",
        "request_too_soon" => "PlacesRequestTooSoon",
        "search_daily_limit" => "PlacesSearchDailyLimit",
        "details_daily_limit" => "PlacesDetailsDailyLimit",
        "photo_daily_limit" => "PlacesPhotoDailyLimit",
        "website_daily_limit" => "PlacesWebsiteDailyLimit",
        "google_rate_limited" => "PlacesGoogleRateLimited",
        "place_not_found" => "PlacesNotFound",
        "not_configured" or "google_forbidden" => "PlacesNotConfigured",
        _ => "PlacesSearchFailed"
    };
}

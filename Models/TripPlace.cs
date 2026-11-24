using System.Globalization;

namespace Packo.Models;

public sealed record TripPlace
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = "landmark";
    public string LocationText { get; init; } = string.Empty;
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? WebsiteUrl { get; init; }
    public string? SourceUrl { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string UserLabel { get; init; } = string.Empty;
    public string UserNote { get; init; } = string.Empty;
    public List<PlaceAttribution> Attributions { get; init; } = [];
    public bool IsGoogle => Id.Length > 7 && Id.StartsWith("google:", StringComparison.Ordinal);
    public bool IsManual => Id.StartsWith("manual:", StringComparison.Ordinal);

    // Google content stays in memory. Only its identifier and the user's own data
    // belong in the durable list; names, addresses and photo references are transient.
    public TripPlace ForStorage() => !IsGoogle ? this : new TripPlace
    {
        Id = Id, Provider = "google", UserLabel = UserLabel, UserNote = UserNote
    };

    public Uri MapUri => IsGoogle ? new("https://www.google.com/maps/search/?api=1&query="
        + Uri.EscapeDataString(string.IsNullOrWhiteSpace(Name) ? "place" : Name)
        + "&query_place_id=" + Uri.EscapeDataString(Id["google:".Length..]))
        : new("https://www.google.com/maps/search/?api=1&query=" +
        Uri.EscapeDataString(Latitude.HasValue && Longitude.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{Latitude.Value},{Longitude.Value}")
            : $"{Name}, {LocationText}".TrimEnd(',', ' ')));

    public static Uri? WebUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
}

public sealed record PlaceAttribution(string Name, string? Url = null);
public sealed record TripPlaceDetails(TripPlace Place, string? PhotoReference = null,
    IReadOnlyList<PlaceAttribution>? PhotoAttributions = null, string? PhotoSourceUrl = null);

public sealed record SavedTripPlace(TripPlace Place, bool Visited = false);

public sealed record TripPlacesState(string ContextKey, IReadOnlyList<TripPlace> Suggestions,
    IReadOnlyList<SavedTripPlace> Saved)
{
    public static TripPlacesState Empty => new(string.Empty, [], []);
}

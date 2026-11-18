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

    public Uri MapUri => new("https://www.google.com/maps/search/?api=1&query=" +
        Uri.EscapeDataString(Latitude.HasValue && Longitude.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{Latitude.Value},{Longitude.Value}")
            : $"{Name}, {LocationText}".TrimEnd(',', ' ')));

    public static Uri? WebUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
}

public sealed record SavedTripPlace(TripPlace Place, bool Visited = false);

public sealed record TripPlacesState(string ContextKey, IReadOnlyList<TripPlace> Suggestions,
    IReadOnlyList<SavedTripPlace> Saved, IReadOnlyList<string>? SeenIds = null)
{
    public static TripPlacesState Empty => new(string.Empty, [], []);
}

using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Packo.Models;
using Packo.Services;
using Packo.Services.Interfaces;

namespace Packo.Popups;

public partial class TripPlaceDetailsPopup : Popup<TripPlace>
{
    private readonly IPlaceSuggestionService _service;
    private readonly CancellationTokenSource _request = new(TimeSpan.FromSeconds(50));
    private TripPlace _place;
    private bool _closing, _loadStarted;
    private static LocalizationResourceManager Strings => LocalizationResourceManager.Instance;

    private TripPlaceDetailsPopup(Page page, TripPlace place, IPlaceSuggestionService service)
    {
        InitializeComponent();
        _place = place;
        _service = service;
        PopupCard.WidthRequest = PopupCardSizing.GetWidth(page);
        PopupCard.MaximumHeightRequest = Math.Max(280, page.Height * 0.85);
        NameLabel.Text = string.IsNullOrWhiteSpace(place.Name) ? Strings["PlacesSavedGoogle"] : place.Name;
        AddressLabel.Text = place.LocationText;
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => _request.Cancel();
    }

    public static async Task<TripPlace?> ShowAsync(Page page, TripPlace place, IPlaceSuggestionService service)
    {
        var popup = new TripPlaceDetailsPopup(page, place, service);
        try { return (await page.ShowPopupAsync<TripPlace>(popup)).Result; }
        finally { popup._request.Cancel(); popup._request.Dispose(); }
    }

    private async Task LoadAsync()
    {
        if (_loadStarted || _closing) return;
        _loadStarted = true;
        var detailsLoaded = false;
        try
        {
            var details = await _service.GetDetailsAsync(_place.Id, Strings.CurrentLanguage, _request.Token);
            if (_closing) return;
            _place = details.Place with { UserLabel = _place.UserLabel, UserNote = _place.UserNote };
            NameLabel.Text = _place.Name;
            AddressLabel.Text = _place.LocationText;
            AddAttributions(Attributions, _place.Attributions);
            detailsLoaded = true;
            if (details.PhotoReference is not null)
            {
                var bytes = await _service.GetPhotoAsync(details.PhotoReference, _request.Token);
                if (_closing) return;
                if (bytes is not null)
                {
                    // A stream source keeps photos in memory instead of MAUI's URI disk cache.
                    Photo.Source = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
                    Photo.IsVisible = true;
                    SemanticProperties.SetDescription(Photo, _place.Name);
                    AddAttributions(PhotoAuthors, details.PhotoAttributions ?? []);
                    if (TripPlace.WebUri(details.PhotoSourceUrl) is { } source)
                        AddAttribution(PhotoAuthors, new PlaceAttribution(Strings["PlacesPhotoSource"], source.AbsoluteUri));
                }
            }
            if (!Photo.IsVisible)
            {
                MessageLabel.Text = Strings["PlacesNoPhotos"];
                MessageLabel.IsVisible = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closing)
            {
                MessageLabel.Text = Strings[ex is PlacesApiException api ? api.ResourceKey
                    : detailsLoaded ? "PlacesPhotoFailed" : "PlacesDetailsFailed"];
                MessageLabel.IsVisible = true;
            }
        }
        finally { if (!_closing) Loading.IsRunning = Loading.IsVisible = false; }
    }

    private static void AddAttributions(VerticalStackLayout layout, IEnumerable<PlaceAttribution> attributions)
    {
        layout.Children.Clear();
        foreach (var attribution in attributions) AddAttribution(layout, attribution);
    }

    private static void AddAttribution(VerticalStackLayout layout, PlaceAttribution attribution)
    {
            var label = new Label { Text = attribution.Name, FontSize = 12 };
            label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#1F1F1F"), Colors.White);
            if (TripPlace.WebUri(attribution.Url) is { } uri)
            {
                label.TextDecorations = TextDecorations.Underline;
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => { try { await Launcher.Default.OpenAsync(uri); } catch { } };
                label.GestureRecognizers.Add(tap);
            }
            layout.Children.Add(label);
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
    {
        if (_closing) return;
        _closing = true;
        _request.Cancel();
        Photo.Source = null;
        await CloseAsync(_place);
    }
}

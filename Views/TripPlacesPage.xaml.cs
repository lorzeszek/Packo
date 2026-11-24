using Packo.Models;
using Packo.Models.DTO;
using Packo.Popups;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;

namespace Packo.Views;

public partial class TripPlacesPage : ContentPage, IQueryAttributable
{
    public const string Route = "TripPlaces";
    private readonly TripPlacesViewModel _viewModel;
    private readonly IPlaceSuggestionService _places;
    private TripDTO? _trip;
    private bool _active, _adding, _showingMessage, _opening;
    private static LocalizationResourceManager Strings => LocalizationResourceManager.Instance;

    public TripPlacesPage(TripPlacesViewModel viewModel, IPlaceSuggestionService places)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _places = places;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("trip", out var value) && value is TripDTO trip) _trip = trip;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_active) return;
        _active = true;
        _viewModel.Activate();
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        _viewModel.SetOnline(Connectivity.Current.NetworkAccess == NetworkAccess.Internet);
        if (_trip is not null) await _viewModel.LoadAsync(_trip);
    }

    protected override void OnDisappearing()
    {
        if (_adding || _showingMessage) { base.OnDisappearing(); return; }
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        _viewModel.Deactivate();
        _active = false;
        base.OnDisappearing();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => _viewModel.SetOnline(e.NetworkAccess == NetworkAccess.Internet));

    private async void OnAddManualClicked(object? sender, EventArgs e)
    {
        if (!_viewModel.CanEdit || _adding) return;
        _adding = true;
        try
        {
            var place = await TripPlaceEditorPopup.ShowAsync(_viewModel.Destination);
            if (place is not null) await _viewModel.AddManualAsync(place);
        }
        finally { _adding = false; }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is TripPlaceCardViewModel card && !card.IsSaved
            && _viewModel.SaveCommand.CanExecute(card)) await _viewModel.SaveCommand.ExecuteAsync(card);
    }
    private async void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (_adding || (sender as BindableObject)?.BindingContext is not TripPlaceCardViewModel card
            || !_viewModel.RemoveCommand.CanExecute(card)) return;
        _adding = true;
        try
        {
            var confirmed = await ConfirmPopup.AskAsync(Strings["PlacesRemoveTitle"],
                string.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings["PlacesRemoveConfirm"], card.DisplayName),
                Strings["PlacesRemove"], Strings["Cancel"]);
            if (confirmed && _viewModel.RemoveCommand.CanExecute(card)) await _viewModel.RemoveCommand.ExecuteAsync(card);
        }
        finally { _adding = false; }
    }
    private async void OnClearListClicked(object? sender, EventArgs e)
    {
        if (_adding || !_viewModel.CanClearSaved) return;
        _adding = true;
        try
        {
            var confirmed = await ConfirmPopup.AskAsync(Strings["PlacesClearList"],
                string.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings["PlacesClearListConfirm"], _viewModel.SavedCount),
                Strings["PlacesClearList"], Strings["Cancel"]);
            if (confirmed) await _viewModel.ClearSavedAsync();
        }
        finally { _adding = false; }
    }
    private async void OnVisitClicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is TripPlaceCardViewModel card
            && _viewModel.VisitCommand.CanExecute(card)) await _viewModel.VisitCommand.ExecuteAsync(card);
    }
    private async void OnMapClicked(object? sender, EventArgs e) => await OpenAsync(sender, place => place.MapUri);
    private async void OnWebsiteClicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not TripPlaceCardViewModel { Place.IsGoogle: true } card)
        {
            await OpenAsync(sender, place => TripPlace.WebUri(place.WebsiteUrl));
            return;
        }
        if (_opening) return;
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet) { await ShowMessageAsync("PlacesLinksOnline"); return; }
        _opening = true;
        try
        {
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var uri = await _places.GetWebsiteAsync(card.Place.Id, request.Token);
            if (uri is null) await ShowMessageAsync("PlacesNoWebsite");
            else if (!await Launcher.Default.OpenAsync(uri)) await ShowMessageAsync("PlacesLinkFailed");
        }
        catch (Exception ex) { await ShowMessageAsync(ex is PlacesApiException api ? api.ResourceKey : "PlacesLinkFailed"); }
        finally { _opening = false; }
    }
    private async void OnSourceClicked(object? sender, EventArgs e) => await OpenAsync(sender, place => TripPlace.WebUri(place.SourceUrl));

    private async void OnDetailsClicked(object? sender, EventArgs e)
    {
        if (_adding || (sender as BindableObject)?.BindingContext is not TripPlaceCardViewModel card) return;
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet) { await ShowMessageAsync("PlacesLinksOnline"); return; }
        _adding = true;
        try
        {
            var place = await TripPlaceDetailsPopup.ShowAsync(this, card.Place, _places);
            if (place is not null) _viewModel.RememberDetails(place);
        }
        finally { _adding = false; }
    }

    private async void OnEditPlaceClicked(object? sender, EventArgs e)
    {
        if (_adding || !_viewModel.CanEdit || (sender as BindableObject)?.BindingContext is not TripPlaceCardViewModel card) return;
        _adding = true;
        try
        {
            var place = await TripPlaceEditorPopup.EditAsync(card.Place);
            if (place is not null) await _viewModel.EditSavedPlaceAsync(place);
        }
        finally { _adding = false; }
    }

    private async void OnAttributionTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is PlaceAttribution attribution && TripPlace.WebUri(attribution.Url) is { } uri)
            try { await Launcher.Default.OpenAsync(uri); } catch { await ShowMessageAsync("PlacesLinkFailed"); }
    }

    private async Task OpenAsync(object? sender, Func<TripPlace, Uri?> selectUri)
    {
        if ((sender as BindableObject)?.BindingContext is not TripPlaceCardViewModel card || selectUri(card.Place) is not { } uri) return;
        await OpenUriAsync(uri);
    }

    private async void OnGoogleMapsClicked(object? sender, EventArgs e) => await OpenUriAsync(new Uri(
        "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(_viewModel.Destination)));

    private async Task OpenUriAsync(Uri uri)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            await ShowMessageAsync("PlacesLinksOnline");
            return;
        }
        try
        {
            // Known web links can be launched directly. Android package visibility can
            // make TryOpenAsync's preliminary handler check return a false negative.
            if (!await Launcher.Default.OpenAsync(uri))
                await ShowMessageAsync("PlacesLinkFailed");
        }
        catch { await ShowMessageAsync("PlacesLinkFailed"); }
    }

    private async Task ShowMessageAsync(string key)
    {
        _showingMessage = true;
        try { await ConfirmPopup.ShowMessageAsync(Strings["PlacesTitle"], Strings[key]); }
        finally { _showingMessage = false; }
    }
}

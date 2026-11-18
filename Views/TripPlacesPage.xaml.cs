using Packo.Models;
using Packo.Models.DTO;
using Packo.Popups;
using Packo.Services;
using Packo.ViewModels;

namespace Packo.Views;

public partial class TripPlacesPage : ContentPage, IQueryAttributable
{
    public const string Route = "TripPlaces";
    private readonly TripPlacesViewModel _viewModel;
    private TripDTO? _trip;
    private bool _active, _adding, _showingMessage;
    private static LocalizationResourceManager Strings => LocalizationResourceManager.Instance;

    public TripPlacesPage(TripPlacesViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
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
        if ((sender as BindableObject)?.BindingContext is TripPlaceCardViewModel card
            && _viewModel.RemoveCommand.CanExecute(card)) await _viewModel.RemoveCommand.ExecuteAsync(card);
    }
    private async void OnVisitClicked(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is TripPlaceCardViewModel card
            && _viewModel.VisitCommand.CanExecute(card)) await _viewModel.VisitCommand.ExecuteAsync(card);
    }
    private async void OnMapClicked(object? sender, EventArgs e) => await OpenAsync(sender, place => place.MapUri);
    private async void OnWebsiteClicked(object? sender, EventArgs e) => await OpenAsync(sender, place => TripPlace.WebUri(place.WebsiteUrl));
    private async void OnSourceClicked(object? sender, EventArgs e) => await OpenAsync(sender, place => TripPlace.WebUri(place.SourceUrl));

    private async Task OpenAsync(object? sender, Func<TripPlace, Uri?> selectUri)
    {
        if ((sender as BindableObject)?.BindingContext is not TripPlaceCardViewModel card || selectUri(card.Place) is not { } uri) return;
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

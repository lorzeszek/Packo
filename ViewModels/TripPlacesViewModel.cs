using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Packo.Extensions;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;

namespace Packo.ViewModels;

public sealed class TripPlaceCardViewModel(TripPlace place, bool saved, bool visited, bool savedView = false) : ObservableObject
{
    private bool _canEdit;
    public TripPlace Place { get; } = place;
    public bool IsSaved { get; } = saved;
    public bool IsVisited { get; } = visited;
    public bool ShowSaveAction => !savedView;
    public bool ShowSavedActions => savedView;
    public bool CanEdit
    {
        get => _canEdit;
        set { if (SetProperty(ref _canEdit, value)) OnPropertyChanged(nameof(CanSave)); }
    }
    public bool CanSave => CanEdit && !IsSaved;
    public string SaveLabel => LocalizationResourceManager.Instance[IsSaved ? "PlacesAlreadySaved" : "PlacesSave"];
    public string VisitLabel => LocalizationResourceManager.Instance[IsVisited ? "PlacesVisited" : "PlacesMarkVisited"];
    public string CategoryLabel => LocalizationResourceManager.Instance["PlacesCategory" + (Place.Category switch
    {
        "culture" => "Culture", "nature" => "Nature", "family" => "Family", "manual" => "Manual", _ => "Landmark"
    })];
    public bool HasDescription => !string.IsNullOrWhiteSpace(Place.Description);
    public bool HasWebsite => TripPlace.WebUri(Place.WebsiteUrl) is not null;
    public bool HasSource => TripPlace.WebUri(Place.SourceUrl) is not null;
}

public sealed class TripPlacesViewModel : ObservableObject
{
    private readonly ITripPlacesRepository _repository;
    private readonly IPlaceSuggestionService _suggestions;
    private readonly ISessionService _session;
    private TripPlacesState _state = TripPlacesState.Empty;
    private TripDTO? _trip;
    private CancellationTokenSource? _request;
    private int _loadVersion;
    private bool _online, _working, _showSaved, _loaded;
    private string _message = string.Empty;
    private static LocalizationResourceManager Strings => LocalizationResourceManager.Instance;

    public TripPlacesViewModel(ITripPlacesRepository repository, IPlaceSuggestionService suggestions, ISessionService session)
    {
        (_repository, _suggestions, _session) = (repository, suggestions, session);
        FindCommand = new AsyncRelayCommand(FindAsync, () => CanFind);
        SaveCommand = new AsyncRelayCommand<TripPlaceCardViewModel>(card => ChangeAsync(card, "save"), _ => CanEdit);
        RemoveCommand = new AsyncRelayCommand<TripPlaceCardViewModel>(card => ChangeAsync(card, "remove"), _ => CanEdit);
        VisitCommand = new AsyncRelayCommand<TripPlaceCardViewModel>(card => ChangeAsync(card, "visit"), _ => CanEdit);
        ShowSuggestionsCommand = new RelayCommand(() => ShowSaved = false);
        ShowSavedCommand = new RelayCommand(() => ShowSaved = true);
    }

    public ObservableRangeCollection<TripPlaceCardViewModel> Items { get; } = new();
    public IAsyncRelayCommand FindCommand { get; }
    public IAsyncRelayCommand<TripPlaceCardViewModel> SaveCommand { get; }
    public IAsyncRelayCommand<TripPlaceCardViewModel> RemoveCommand { get; }
    public IAsyncRelayCommand<TripPlaceCardViewModel> VisitCommand { get; }
    public IRelayCommand ShowSuggestionsCommand { get; }
    public IRelayCommand ShowSavedCommand { get; }
    public string Destination => _trip?.Destination ?? string.Empty;
    public bool IsOffline => !_online;
    public bool IsWorking => _working;
    public bool CanEdit => _loaded && !_working && IsCurrentAccount;
    public bool CanFind => CanEdit && _online;
    public bool ShowSuggestions => !_showSaved;
    public bool ShowSaved
    {
        get => _showSaved;
        set { if (SetProperty(ref _showSaved, value)) Refresh(); }
    }
    public string SavedTabLabel => string.Format(Strings["PlacesSavedCount"], _state.Saved.Count);
    public string FindLabel => Strings[CurrentSuggestions.Count == 0 ? "PlacesFind" : "PlacesFindMore"];
    public string EmptyText => Strings[ShowSaved ? "PlacesSavedEmpty" : "PlacesSuggestionsEmpty"];
    public string Message { get => _message; private set { SetProperty(ref _message, value); OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    private bool IsCurrentAccount => _trip is not null && _session.LocalUserId == _trip.LocalUserId && !_session.GlobalIsBusy;
    private IReadOnlyList<TripPlace> CurrentSuggestions => _trip is not null
        && _state.ContextKey == PlaceSuggestionService.ContextKey(_trip, Strings.CurrentLanguage) ? _state.Suggestions : [];

    public async Task LoadAsync(TripDTO trip)
    {
        var version = ++_loadVersion;
        Cancel();
        _request = null;
        _working = false;
        _trip = trip.CopyForEditing();
        _state = TripPlacesState.Empty;
        _loaded = false;
        Message = string.Empty;
        Refresh();
        if (!IsCurrentAccount) { Message = Strings["PlacesUnavailable"]; return; }
        SetWorking(true);
        try
        {
            var state = await _repository.LoadAsync(_trip);
            if (version != _loadVersion || !IsCurrentAccount) return;
            _state = state;
            _loaded = true;
            _showSaved = state.Saved.Count > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Places load: {ex.GetType().Name}");
            if (version == _loadVersion && IsCurrentAccount) Message = Strings["PlacesLoadFailed"];
        }
        finally { if (version == _loadVersion) { SetWorking(false); Refresh(); } }
    }

    public void SetOnline(bool online)
    {
        _online = online;
        if (!online) Cancel();
        Refresh();
    }

    public void Activate()
    {
        Strings.PropertyChanged += LanguageChanged;
        if (_session is INotifyPropertyChanged session) session.PropertyChanged += SessionChanged;
    }

    public void Deactivate()
    {
        Strings.PropertyChanged -= LanguageChanged;
        if (_session is INotifyPropertyChanged session) session.PropertyChanged -= SessionChanged;
        Cancel();
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) { Message = string.Empty; Refresh(); }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_trip is not null && _session.LocalUserId != _trip.LocalUserId)
        {
            Cancel();
            _loaded = false;
            _state = TripPlacesState.Empty;
            Message = Strings["PlacesUnavailable"];
        }
        Refresh();
    }

    public void Cancel() => _request?.Cancel();

    private async Task FindAsync()
    {
        if (!CanFind || _trip is null) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        _request = request;
        var version = _loadVersion;
        var trip = _trip.CopyForEditing();
        var language = Strings.CurrentLanguage;
        var context = PlaceSuggestionService.ContextKey(trip, language);
        var excluded = (_state.ContextKey == context ? _state.SeenIds ?? [] : [])
            .Concat(_state.Saved.Select(x => x.Place.Id)).Distinct().ToList();
        Message = string.Empty;
        SetWorking(true);
        try
        {
            var places = await _suggestions.FindAsync(trip, language, excluded, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (version != _loadVersion || !IsCurrentAccount || !ReferenceEquals(_request, request) || Strings.CurrentLanguage != language) return;
            if (places.Count == 0) { Message = Strings["PlacesNoMore"]; return; }
            var state = await _repository.ReplaceSuggestionsAsync(trip, context, places);
            if (version == _loadVersion && IsCurrentAccount && ReferenceEquals(_request, request)) _state = state;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_request, request) && _online && IsCurrentAccount) Message = Strings["PlacesSearchFailed"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Places search: {ex.GetType().Name}");
            if (ReferenceEquals(_request, request) && IsCurrentAccount) Message = Strings[_online ? "PlacesSearchFailed" : "PlacesOffline"];
        }
        finally
        {
            if (ReferenceEquals(_request, request)) { _request = null; SetWorking(false); Refresh(); }
        }
    }

    public async Task AddManualAsync(TripPlace place)
    {
        if (!CanEdit || _trip is null) return;
        await ChangeAsync(new TripPlaceCardViewModel(place, false, false), "save");
        ShowSaved = true;
    }

    private async Task ChangeAsync(TripPlaceCardViewModel? card, string action)
    {
        if (!CanEdit || _trip is null || card is null) return;
        var version = _loadVersion;
        Message = string.Empty;
        SetWorking(true);
        try
        {
            var state = action switch
            {
                "remove" => await _repository.RemoveAsync(_trip, card.Place.Id),
                "visit" => await _repository.SetVisitedAsync(_trip, card.Place.Id, !card.IsVisited),
                _ => await _repository.SaveAsync(_trip, card.Place)
            };
            if (version == _loadVersion && IsCurrentAccount) _state = state;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Places save: {ex.GetType().Name}");
            if (version == _loadVersion && IsCurrentAccount) Message = Strings["PlacesSaveFailed"];
        }
        finally { if (version == _loadVersion) { SetWorking(false); Refresh(); } }
    }

    private void SetWorking(bool value) { _working = value; RefreshCommands(); }
    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(IsWorking)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanFind));
        FindCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged(); VisitCommand.NotifyCanExecuteChanged();
        foreach (var item in Items) item.CanEdit = CanEdit;
    }
    private void Refresh()
    {
        var saved = _state.Saved.ToDictionary(x => x.Place.Id);
        Items.ReplaceRange(ShowSaved
            ? _state.Saved.Select(x => new TripPlaceCardViewModel(x.Place, true, x.Visited, savedView: true))
            : CurrentSuggestions.Select(x => new TripPlaceCardViewModel(x, saved.ContainsKey(x.Id), saved.GetValueOrDefault(x.Id)?.Visited ?? false)));
        foreach (var name in new[] { nameof(Destination), nameof(IsOffline), nameof(ShowSaved), nameof(ShowSuggestions),
            nameof(SavedTabLabel), nameof(FindLabel), nameof(EmptyText) }) OnPropertyChanged(name);
        RefreshCommands();
    }
}

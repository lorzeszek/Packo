using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Packo.Interfaces;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.Enums;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using System.Collections.ObjectModel;

namespace Packo.ViewModels
{
    //public partial class ChildAgeItem : ObservableObject
    //{
    //    private int _age;

    //    public int Age
    //    {
    //        get => _age;
    //        set => SetProperty(ref _age, value);
    //    }

    //    public ChildAgeItem(int age)
    //    {
    //        Age = age;
    //    }

    //    [RelayCommand]
    //    private void IncreaseAge()
    //    {
    //        Age++;
    //    }

    //    [RelayCommand]
    //    private void DecreaseAge()
    //    {
    //        if (Age > 0)
    //            Age--;
    //    }
    //}

    public partial class TripSetupViewModel : BaseViewModel
    {
        private readonly IPackingSuggestionService _packingSuggestionService;
        private readonly ITripClassificationService _tripClassificationService;

        public TripSetupViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, IPackingSuggestionService packingSuggestionService, ITripClassificationService tripClassificationService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _packingSuggestionService = packingSuggestionService;
            _tripClassificationService = tripClassificationService;

            TransportOptions = new ObservableCollection<TransportOptionItem>
            {
                new("Plane"),
                new("Train"),
                new("Bus"),
                new("Car")
            };

            foreach (var transportOption in TransportOptions)
                transportOption.PropertyChanged += OnTransportOptionChanged;
        }

        // ====== INPUT ======
        [ObservableProperty]
        [NotifyCanExecuteChangedFor("CreateTripCommand")]
        private string destination = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor("CreateTripCommand")]
        private DateTime startDate = DateTime.Today;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor("CreateTripCommand")]
        private DateTime endDate = DateTime.Today.AddDays(1);

        [ObservableProperty]
        [NotifyCanExecuteChangedFor("CreateTripCommand")]
        private int adultsCount = 1;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor("CreateTripCommand")]
        private List<int> childrenAge = new List<int>();

        public ObservableCollection<ChildAgeItem> Children { get; } = new();

        public ObservableCollection<TransportOptionItem> TransportOptions { get; }

        [ObservableProperty]
        private List<string> selectedTransportTypes = new List<string>();

        public int ChildrenCount => Children.Count;

        public bool HasChildren => ChildrenCount > 0;

        partial void OnStartDateChanged(DateTime value)
        {
            if (value < DateTime.Today)
                StartDate = DateTime.Today;

            if (EndDate < value)
                EndDate = value;
        }

        partial void OnEndDateChanged(DateTime value)
        {
            if (value < StartDate)
                EndDate = StartDate;
        }

        [RelayCommand]
        private void IncreaseAdultsCount()
        {
            AdultsCount++;
        }

        [RelayCommand]
        private void DecreaseAdultsCount()
        {
            if (AdultsCount > 1)
                AdultsCount--;
        }

        [RelayCommand]
        private void IncreaseChildrenCount()
        {
            var child = new ChildAgeItem(1);
            child.PropertyChanged += OnChildAgeChanged;
            Children.Add(child);
            NotifyChildrenChanged();
        }

        [RelayCommand]
        private void DecreaseChildrenCount()
        {
            if (ChildrenCount == 0)
                return;

            var child = Children[^1];
            child.PropertyChanged -= OnChildAgeChanged;
            Children.RemoveAt(Children.Count - 1);
            NotifyChildrenChanged();
        }

        private void OnChildAgeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChildAgeItem.Age))
                SyncChildrenAge();
        }

        private void NotifyChildrenChanged()
        {
            OnPropertyChanged(nameof(ChildrenCount));
            OnPropertyChanged(nameof(HasChildren));
            SyncChildrenAge();
        }

        private void SyncChildrenAge()
        {
            ChildrenAge = Children.Select(child => child.Age).ToList();
        }

        private void OnTransportOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TransportOptionItem.IsSelected))
                SyncSelectedTransportTypes();
        }

        private void SyncSelectedTransportTypes()
        {
            SelectedTransportTypes = TransportOptions
                .Where(option => option.IsSelected)
                .Select(option => option.Name)
                .ToList();
        }

        // ====== COMMAND ======

        [RelayCommand(CanExecute = nameof(CanCreateTrip))]
        private async Task CreateTripAsync()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Session.GlobalIsBusy = true;
            });

            if (Session.LocalUserId == null)
            {
                var localUser = await _localUserService.GetOrCreateLocalUserAsync();

                Session.SetLocalUser(localUser.LocalUserId);
            }

            var classification = await _tripClassificationService.ClassifyTripAsync(Destination, StartDate, EndDate, SelectedTransportTypes);

            Enum.TryParse<CoverThemeType>(classification.CoverTheme, true, out var coverTheme);

            var trip = new TripDTO
            {
                LocalUserId = Session.LocalUserId,
                RemoteUserId = Session.UserId,
                CreatedDate = DateTime.Now,
                StartDate = StartDate,
                EndDate = EndDate,
                Destination = Destination,
                IsActive = true,
                CoverTheme = coverTheme
            };

            //var renderer = new TripCoverRenderer();
            //var bitmap = renderer.Render(trip);

            //var path = SaveBitmap(bitmap, trip);

            //trip.CoverImagePath = path;

            await _tripRepository.AddTripAsync(trip);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Session.GlobalIsBusy = false;
            });

            var proposeListPopup = new ConfirmPopup("Lista pakowania", "Czy chcesz wygenerować listę rzeczy do spakowania?");

            var result = await Application.Current.MainPage.ShowPopupAsync<bool>(proposeListPopup);

            var proposeListPopupResult = result.Result;

            if (proposeListPopupResult)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Session.GlobalIsBusy = true;
                });

                try
                {
                    var proposeListItems = await _packingSuggestionService.GenerateItemsAsync(Destination, StartDate, EndDate, AdultsCount, ChildrenAge, SelectedTransportTypes);

                    var addSelectedItemsPopup = new PackingItemsPopup(proposeListItems);

                    _ = Application.Current.MainPage.ShowPopupAsync(addSelectedItemsPopup);

                    var selectedItemsNames = await addSelectedItemsPopup.ResultSource.Task;

                    if (selectedItemsNames?.Count > 0)
                    {
                        var createdTrip = await _tripRepository.GetTripAsync(trip);

                        var newPackingItems = selectedItemsNames.Select(itemName => new PackingItemDTO { Name = itemName, Category = 3, RemoteTripId = createdTrip.RemoteTripId, LocalTripId = createdTrip.LocalTripId, RemoteUserId = Session.UserId, LocalUserId = Session.LocalUserId, CreatedDate = DateTime.Now }).ToList();

                        await _packingItemRepository.AddPackingItemsAsync(newPackingItems);
                    }
                    else
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Handle error
                }
                finally
                {
                    Session.GlobalIsBusy = false;
                }
            }
            //else
            //{
            //    return;
            //}

            // Pop TripSetupPage off the Home tab's stack before switching tabs
            await Shell.Current.Navigation.PopAsync(false);
            await Shell.Current.GoToAsync("//TripList");
        }

        private bool CanCreateTrip()
            => !string.IsNullOrWhiteSpace(Destination)
               && EndDate >= StartDate;
    }
}

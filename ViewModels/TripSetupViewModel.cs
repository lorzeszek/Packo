using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Packo.Interfaces;
using Packo.Helpers;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.Enums;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.Views;
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

    public partial class TripSetupViewModel : BaseViewModel, IQueryAttributable
    {
        private TripDTO? _editingTrip;
        public bool IsEditing => _editingTrip != null;
        public DateTime MinimumStartDate => IsEditing ? new DateTime(1900, 1, 1) : DateTime.Today;
        public string PageTitle => LocalizationResourceManager.Instance[IsEditing ? "EditTrip" : "PlanTrip"];
        public string Heading => LocalizationResourceManager.Instance[IsEditing ? "EditTrip" : "TripSetupHeading"];
        public string Intro => LocalizationResourceManager.Instance[IsEditing ? "EditTripIntro" : "TripSetupIntro"];
        public string SubmitText => LocalizationResourceManager.Instance[IsEditing ? "SaveChanges" : "CreatePackingList"];

        public void ApplyQueryAttributes(IDictionary<string, object> query)
        {
            if (!query.TryGetValue("editTrip", out var value) || value is not TripDTO trip)
                return;

            // Keep form changes separate from the trip displayed in the list until saved.
            _editingTrip = System.Text.Json.JsonSerializer.Deserialize<TripDTO>(
                System.Text.Json.JsonSerializer.Serialize(trip))!;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(MinimumStartDate));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(Heading));
            OnPropertyChanged(nameof(Intro));
            OnPropertyChanged(nameof(SubmitText));
            Destination = trip.Destination;
            StartDate = trip.StartDate?.Date ?? DateTime.Today;
            StartTime = trip.StartDate?.TimeOfDay ?? TimeSpan.Zero;
            EndDate = trip.EndDate?.Date ?? StartDate;
            EndTime = trip.EndDate?.TimeOfDay ?? new TimeSpan(23, 59, 0);
            AdultsCount = trip.AdultsCount;
            foreach (var child in Children)
                child.PropertyChanged -= OnChildAgeChanged;
            Children.Clear();
            foreach (var age in trip.ChildrenAges)
            {
                var child = new ChildAgeItem(age);
                child.PropertyChanged += OnChildAgeChanged;
                Children.Add(child);
            }
            NotifyChildrenChanged();
            foreach (var option in TransportOptions)
                option.IsSelected = trip.SelectedTransportTypes.Any(option.MatchesStoredName);
            SyncSelectedTransportTypes();
            SelectedTripTypeOption = TripTypeOptions.FirstOrDefault(option => option.Value == trip.TripType)
                ?? TripTypeOptions.First();
        }
        private readonly ITripClassificationService _tripClassificationService;

        public TripSetupViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, ITripClassificationService tripClassificationService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _tripClassificationService = tripClassificationService;

            TransportOptions = new ObservableCollection<TransportOptionItem>
            {
                new("Plane"),
                new("Train"),
                new("Bus"),
                new("Car")
            };

            TripTypeOptions = new ObservableCollection<TripTypeOption>
            {
                new(TripType.Leisure, "TripTypeLeisure"),
                new(TripType.Sightseeing, "TripTypeSightseeing"),
                new(TripType.Business, "TripTypeBusiness"),
                new(TripType.Family, "TripTypeFamily"),
                new(TripType.ActiveAndSport, "TripTypeActiveAndSport"),
                new(TripType.WinterSports, "TripTypeWinterSports"),
                new(TripType.RoadTrip, "TripTypeRoadTrip"),
                new(TripType.Camping, "TripTypeCamping"),
                new(TripType.Wellness, "TripTypeWellness"),
                new(TripType.Romantic, "TripTypeRomantic"),
                new(TripType.Celebration, "TripTypeCelebration"),
                new(TripType.FestivalOrEvent, "TripTypeFestivalOrEvent"),
                new(TripType.Other, "TripTypeOther")
            };
            SelectedTripTypeOption = TripTypeOptions.First();

            foreach (var transportOption in TransportOptions)
                transportOption.PropertyChanged += OnTransportOptionChanged;
            LocalizationResourceManager.Instance.PropertyChanged += (_, _) =>
            {
                SyncSelectedTransportTypes();
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(Heading));
                OnPropertyChanged(nameof(Intro));
                OnPropertyChanged(nameof(SubmitText));
            };
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
        private TimeSpan startTime = TimeSpan.Zero;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor("CreateTripCommand")]
        private TimeSpan endTime = new(23, 59, 0);

        public DateTime StartDateTime => TripSchedule.Combine(StartDate, StartTime);
        public DateTime EndDateTime => TripSchedule.Combine(EndDate, EndTime);
        public bool HasScheduleError => !TripSchedule.IsValid(StartDateTime, EndDateTime);

        [ObservableProperty]
        [NotifyCanExecuteChangedFor("CreateTripCommand")]
        private int adultsCount = 1;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor("CreateTripCommand")]
        private List<int> childrenAge = new List<int>();

        public ObservableCollection<ChildAgeItem> Children { get; } = new();

        public ObservableCollection<TransportOptionItem> TransportOptions { get; }

        public ObservableCollection<TripTypeOption> TripTypeOptions { get; }

        [ObservableProperty]
        private TripTypeOption selectedTripTypeOption = null!;

        [ObservableProperty]
        private List<string> selectedTransportTypes = new List<string>();

        public int ChildrenCount => Children.Count;

        public bool HasChildren => ChildrenCount > 0;

        partial void OnStartDateChanged(DateTime value)
        {
            var normalized = value.Date;
            if (normalized != value)
            {
                StartDate = normalized;
                return;
            }

            if (!IsEditing && normalized < DateTime.Today)
            {
                StartDate = DateTime.Today;
                return;
            }

            if (EndDate.Date < normalized)
                EndDate = normalized;

            NotifyScheduleChanged();
        }

        partial void OnEndDateChanged(DateTime value)
        {
            if (value != value.Date)
            {
                EndDate = value.Date;
                return;
            }

            if (value.Date < StartDate.Date)
                EndDate = StartDate.Date;

            NotifyScheduleChanged();
        }

        partial void OnStartTimeChanged(TimeSpan value) => NotifyScheduleChanged();
        partial void OnEndTimeChanged(TimeSpan value) => NotifyScheduleChanged();

        private void NotifyScheduleChanged()
        {
            OnPropertyChanged(nameof(StartDateTime));
            OnPropertyChanged(nameof(EndDateTime));
            OnPropertyChanged(nameof(HasScheduleError));
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
            if (IsEditing)
            {
                await SaveTripAsync();
                return;
            }
            TripDTO trip;
            Session.GlobalIsBusy = true;
            try
            {
                if (Session.LocalUserId == null)
                {
                    var localUser = await _localUserService.GetOrCreateLocalUserAsync();
                    Session.SetLocalUser(localUser.LocalUserId);
                }

                var classification = await _tripClassificationService.ClassifyTripAsync(
                    Destination, StartDateTime, EndDateTime, SelectedTransportTypes);
                Enum.TryParse<CoverThemeType>(classification.CoverTheme, true, out var coverTheme);

                trip = new TripDTO
                {
                    LocalUserId = Session.LocalUserId,
                    RemoteUserId = Session.UserId,
                    CreatedDate = DateTime.Now,
                    StartDate = StartDateTime,
                    EndDate = EndDateTime,
                    Destination = Destination,
                    AdultsCount = AdultsCount,
                    ChildrenAges = ChildrenAge.ToList(),
                    SelectedTransportTypes = SelectedTransportTypes.ToList(),
                    TripType = SelectedTripTypeOption.Value,
                    IsActive = EndDateTime > DateTime.Now,
                    CoverTheme = coverTheme
                };

                trip.LocalTripId = await _tripRepository.AddTripAsync(trip);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                var strings = LocalizationResourceManager.Instance;
                await ConfirmPopup.ShowMessageAsync(strings["Error"], strings["SaveTripError"]);
                return;
            }
            finally
            {
                Session.GlobalIsBusy = false;
            }

            await Shell.Current.Navigation.PopAsync(false);
            await Shell.Current.GoToAsync("//TripList");
            await Shell.Current.GoToAsync(StartViewModel.Route, new ShellNavigationQueryParameters
            {
                ["trip"] = trip
            });
        }

        private async Task SaveTripAsync()
        {
            var trip = _editingTrip!;
            trip.Destination = Destination.Trim();
            trip.StartDate = StartDateTime;
            trip.EndDate = EndDateTime;
            trip.IsActive = EndDateTime > DateTime.Now;
            trip.AdultsCount = AdultsCount;
            trip.ChildrenAges = ChildrenAge.ToList();
            trip.SelectedTransportTypes = SelectedTransportTypes.ToList();
            trip.TripType = SelectedTripTypeOption.Value;
            Session.GlobalIsBusy = true;
            try
            {
                await _tripRepository.UpdateTripAsync(trip);
            }
            catch (Exception)
            {
                await ConfirmPopup.ShowMessageAsync(LocalizationResourceManager.Instance["Error"],
                    LocalizationResourceManager.Instance["SaveTripError"]);
                return;
            }
            finally
            {
                Session.GlobalIsBusy = false;
            }
            await Shell.Current.GoToAsync("..");
        }

        private bool CanCreateTrip()
            => !string.IsNullOrWhiteSpace(Destination)
               && TripSchedule.IsValid(StartDateTime, EndDateTime);
    }
}

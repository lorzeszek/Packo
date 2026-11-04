using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Models.DTO;
using Packo.Models.Enums;
using Packo.Services;
using Packo.Helpers;
using System;

namespace Packo.ViewModels
{
    public partial class TripViewModel : ObservableObject
    {
        //public Trip TripModel { get; }
        public TripDTO TripModel { get; }
        private readonly TimeProvider _timeProvider;
        private DateTime _now;

        public TripStatus Status => TripSchedule.GetStatus(TripModel.StartDate, TripModel.EndDate, _now);
        public bool IsCompleted => Status == TripStatus.Completed;
        public string StatusLabel => Status switch
        {
            TripStatus.Upcoming => LocalizationResourceManager.Instance["TripStatusUpcoming"],
            TripStatus.Current => LocalizationResourceManager.Instance["TripStatusCurrent"],
            TripStatus.Completed => LocalizationResourceManager.Instance["PastTrip"],
            _ => string.Empty
        };

        public string Destination => TripModel.Destination;
        public string CoverImageSource => TripModel.CoverTheme switch
        {
            CoverThemeType.mountains_winter => "mountains_winter.png",
            CoverThemeType.mountains_summer_01 => "mountains_summer_01.png",
            CoverThemeType.mountains_summer_02 => "mountains_summer_02.png",
            CoverThemeType.road_trip => "road_trip.png",
            CoverThemeType.summer_beach_01 => "summer_beach_01.png",
            CoverThemeType.summer_beach_02 => "summer_beach_02.png",
            CoverThemeType.business_trip => "business_trip.png",
            CoverThemeType.weekend_city_break => "weekend_city_break.png",
            CoverThemeType.big_city => "big_city.png",
            CoverThemeType.camping => "camping.png",
            _ => "big_city.png"
        };

        public string TripDatesRange => GetTripDatesRange();
        public string TripDaysToGo => GetTripDaysToGo();
        public string TripDaysRemaining => GetTripDaysRemaining();
        public string TripCountdown => Status switch
        {
            TripStatus.Completed => LocalizationResourceManager.Instance["TripEnded"],
            TripStatus.Current => TripDaysRemaining,
            TripStatus.Upcoming => TripDaysToGo,
            _ => string.Empty
        };

        private string _packingSummary = string.Empty;
        public string PackingSummary
        {
            get => _packingSummary;
            set
            {
                if (_packingSummary != value)
                {
                    _packingSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        //public string BackgroundImage => $"https://cdn.example.com/trips/{Model.Destination}.jpg";
        public string BackgroundImage => "italy.webp";

        //public TripViewModel(Trip trip)
        public TripViewModel(TripDTO trip, TimeProvider? timeProvider = null)
        {
            TripModel = trip;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _now = _timeProvider.GetLocalNow().DateTime;
            TripModel.IsActive = TripModel.EndDate > _now;
            LocalizationResourceManager.Instance.PropertyChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(TripDatesRange));
            OnPropertyChanged(nameof(TripDaysToGo));
            OnPropertyChanged(nameof(TripDaysRemaining));
            OnPropertyChanged(nameof(TripCountdown));
            OnPropertyChanged(nameof(StatusLabel));
        }

        public bool RefreshSchedule()
        {
            var previousStatus = Status;
            var now = _timeProvider.GetLocalNow().DateTime;
            if (now.Ticks / TimeSpan.TicksPerMinute == _now.Ticks / TimeSpan.TicksPerMinute
                && TripSchedule.GetStatus(TripModel.StartDate, TripModel.EndDate, now) == previousStatus)
                return false;

            _now = now;
            NotifyScheduleChanged();
            return Status != previousStatus;
        }

        private void NotifyScheduleChanged()
        {
            TripModel.IsActive = TripModel.EndDate > _now;
            OnPropertyChanged(nameof(TripModel));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(TripDatesRange));
            OnPropertyChanged(nameof(TripDaysToGo));
            OnPropertyChanged(nameof(TripDaysRemaining));
            OnPropertyChanged(nameof(TripCountdown));
        }

        private string GetTripDatesRange()
        {
            var startDate = TripModel.StartDate;
            var endDate = TripModel.EndDate;

            if (startDate.HasValue && endDate.HasValue && startDate.Value.Date == endDate.Value.Date)
                return $"{startDate:dd MMM yyyy} · {startDate:HH:mm}–{endDate:HH:mm}";

            if (startDate.HasValue && !endDate.HasValue)
            {
                return $"{startDate:dd MMM yyyy, HH:mm}";
            }

            return $"{startDate:dd MMM yyyy, HH:mm} – {endDate:dd MMM yyyy, HH:mm}";
        }

        private string GetTripDaysToGo()
        {
            var startDate = TripModel.StartDate;

            if (!startDate.HasValue)
                return string.Empty;

            var days = (startDate.Value.Date - _now.Date).Days;

            if (days <= 0)
                return string.Format(System.Globalization.CultureInfo.CurrentUICulture,
                    LocalizationResourceManager.Instance["StartsAt"], startDate.Value.ToString("HH:mm"));

            var key = days == 1 ? "DayToGo" : "DaysToGo";
            return string.Format(System.Globalization.CultureInfo.CurrentUICulture, LocalizationResourceManager.Instance[key], days);
        }

        private string GetTripDaysRemaining()
        {
            var endDate = TripModel.EndDate;

            if (!endDate.HasValue)
                return string.Empty;

            var days = (endDate.Value.Date - _now.Date).Days;

            if (days < 0)
                return LocalizationResourceManager.Instance["TripEnded"];

            if (days == 0)
                return string.Format(System.Globalization.CultureInfo.CurrentUICulture,
                    LocalizationResourceManager.Instance["EndsAt"], endDate.Value.ToString("HH:mm"));

            var key = days == 1 ? "DayRemaining" : "DaysRemaining";
            return string.Format(System.Globalization.CultureInfo.CurrentUICulture, LocalizationResourceManager.Instance[key], days);
        }

        public void UpdateFromTrip(TripDTO trip)
        {
            TripModel.Destination = trip.Destination;
            TripModel.AdultsCount = trip.AdultsCount;
            TripModel.ChildrenAges = trip.ChildrenAges.ToList();
            TripModel.SelectedTransportTypes = trip.SelectedTransportTypes.ToList();
            TripModel.TripType = trip.TripType;
            TripModel.IsInTrash = trip.IsInTrash;
            TripModel.StartDate = trip.StartDate;
            TripModel.EndDate = trip.EndDate;
            TripModel.RemoteUserId = trip.RemoteUserId;
            TripModel.CoverTheme = trip.CoverTheme;

            OnPropertyChanged(nameof(Destination));
            OnPropertyChanged(nameof(CoverImageSource));
            _now = _timeProvider.GetLocalNow().DateTime;
            NotifyScheduleChanged();
        }

        //public event PropertyChangedEventHandler? PropertyChanged;

        //protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        //{
        //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        //}
    }
}

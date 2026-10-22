using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Models.DTO;
using Packo.Models.Enums;
using Packo.Services;
using System;

namespace Packo.ViewModels
{
    public partial class TripViewModel : ObservableObject
    {
        //public Trip TripModel { get; }
        public TripDTO TripModel { get; }

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
        public string TripCountdown => HasTripStarted() ? TripDaysRemaining : TripDaysToGo;

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
        public TripViewModel(TripDTO trip)
        {
            TripModel = trip;
            LocalizationResourceManager.Instance.PropertyChanged += OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(TripDatesRange));
            OnPropertyChanged(nameof(TripDaysToGo));
            OnPropertyChanged(nameof(TripDaysRemaining));
            OnPropertyChanged(nameof(TripCountdown));
        }

        private string GetTripDatesRange()
        {
            var startDate = TripModel.StartDate;
            var endDate = TripModel.EndDate;

            if (startDate.HasValue && endDate.HasValue && startDate.Value.Year == endDate.Value.Year)
            {
                return $"{startDate:dd MMM} - {endDate:dd MMM} {startDate:yyyy}";
            }
            else if (startDate.HasValue && !endDate.HasValue)
            {
                return $"{startDate:dd MMM yyyy}";
            }
            else
                return $"{startDate:dd MMM yyyy} - {endDate:dd MMM yyyy}";
        }

        private bool HasTripStarted()
        {
            var startDate = TripModel.StartDate;
            return startDate.HasValue && startDate.Value.Date <= DateTime.Today;
        }

        private string GetTripDaysToGo()
        {
            var startDate = TripModel.StartDate;

            if (!startDate.HasValue)
                return string.Empty;

            var days = (startDate.Value.Date - DateTime.Today).Days;

            if (days <= 0)
                return LocalizationResourceManager.Instance["StartsToday"];

            var key = days == 1 ? "DayToGo" : "DaysToGo";
            return string.Format(System.Globalization.CultureInfo.CurrentUICulture, LocalizationResourceManager.Instance[key], days);
        }

        private string GetTripDaysRemaining()
        {
            var endDate = TripModel.EndDate;

            if (!endDate.HasValue)
                return string.Empty;

            var days = (endDate.Value.Date - DateTime.Today).Days;

            if (days < 0)
                return LocalizationResourceManager.Instance["TripEnded"];

            if (days == 0)
                return LocalizationResourceManager.Instance["LastDay"];

            var key = days == 1 ? "DayRemaining" : "DaysRemaining";
            return string.Format(System.Globalization.CultureInfo.CurrentUICulture, LocalizationResourceManager.Instance[key], days);
        }

        public void UpdateFromTrip(TripDTO trip)
        {
            TripModel.Destination = trip.Destination;
            TripModel.IsActive = trip.IsActive;
            TripModel.IsInTrash = trip.IsInTrash;
            TripModel.StartDate = trip.StartDate;
            TripModel.EndDate = trip.EndDate;
            TripModel.ModifiedDate = trip.ModifiedDate;
            TripModel.RemoteUserId = trip.RemoteUserId;

            OnPropertyChanged(nameof(Destination));
            OnPropertyChanged(nameof(TripModel.IsActive));
            OnPropertyChanged(nameof(TripModel.IsInTrash));
            OnPropertyChanged(nameof(TripModel.StartDate));
            OnPropertyChanged(nameof(TripModel.EndDate));
            OnPropertyChanged(nameof(TripModel.ModifiedDate));
            OnPropertyChanged(nameof(TripModel.RemoteUserId));
            OnPropertyChanged(nameof(TripDatesRange));
            OnPropertyChanged(nameof(TripDaysToGo));
            OnPropertyChanged(nameof(TripDaysRemaining));
            OnPropertyChanged(nameof(TripCountdown));
        }

        //public event PropertyChangedEventHandler? PropertyChanged;

        //protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        //{
        //    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        //}
    }
}

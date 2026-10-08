using CommunityToolkit.Mvvm.ComponentModel;
using Packo.Models.DTO;
using System;

namespace Packo.ViewModels
{
    public partial class TripViewModel : ObservableObject
    {
        //public Trip TripModel { get; }
        public TripDTO TripModel { get; }

        public string Destination => TripModel.Destination;

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
                return "Starts today";

            return days == 1 ? "1 day to go" : $"{days} days to go";
        }

        private string GetTripDaysRemaining()
        {
            var endDate = TripModel.EndDate;

            if (!endDate.HasValue)
                return string.Empty;

            var days = (endDate.Value.Date - DateTime.Today).Days;

            if (days < 0)
                return "Trip ended";

            if (days == 0)
                return "Last day";

            return days == 1 ? "1 day remaining" : $"{days} days remaining";
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

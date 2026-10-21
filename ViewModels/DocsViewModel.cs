using CommunityToolkit.Mvvm.Input;
using Packo.Extensions;
using Packo.Interfaces;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.Services;
using System.Globalization;

namespace Packo.ViewModels
{
    public class DocsViewModel : BaseViewModel
    {
        private int _localTripId { get; set; }
        public ObservableRangeCollection<TripDocument> Docs { get; } = new();
        public IAsyncRelayCommand AddDocumentCommand => new AsyncRelayCommand(AddDocumentAsync);
        public IAsyncRelayCommand<TripDocument> DeleteDocumentCommand => new AsyncRelayCommand<TripDocument>(DeleteDocumentAsync);
        public IAsyncRelayCommand<TripDocument> OpenDocumentCommand => new AsyncRelayCommand<TripDocument>(OpenDocumentAsync);

        public DocsViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService)
            : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
        }

        private async Task OpenDocumentAsync(TripDocument? document)
        {
            if (document == null)
                return;

            if (!File.Exists(document.LocalPath))
            {
                await Shell.Current.DisplayAlertAsync(
                    T("Error"),
                    T("FileNotFound"),
                    T("Ok"));

                return;
            }

            await Launcher.Default.OpenAsync(
                new OpenFileRequest(
                    document.FileName,
                    new ReadOnlyFile(document.LocalPath)));
        }

        private async Task DeleteDocumentAsync(TripDocument? document)
        {
            if (document == null)
                return;

            var confirmed = await Shell.Current.DisplayAlertAsync(
            T("DeleteDocument"),
            string.Format(CultureInfo.CurrentUICulture, T("DeleteDocumentConfirm"), document.FileName),
            T("Delete"),
            T("Cancel"));

            if (!confirmed)
                return;

            if (File.Exists(document.LocalPath))
            {
                File.Delete(document.LocalPath);
            }

            await _tripRepository.DeleteDocAsync(document.Id);

            Docs.Remove(document);
        }

        private async Task AddDocumentAsync()
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = T("ChooseDocument"),
                FileTypes = FilePickerFileType.Pdf
            });

            if (result == null)
                return;

            var fileInfo = new FileInfo(result.FullPath);

            var alreadyExists = Docs.Any(x =>
                x.FileName.Equals(result.FileName, StringComparison.OrdinalIgnoreCase) &&
                x.FileSize == fileInfo.Length);

            if (alreadyExists)
            {
                await Shell.Current.DisplayAlert(
                    T("DuplicateDocument"),
                    T("DuplicateDocumentMessage"),
                    T("Ok"));

                return;
            }

            var documentsPath = Path.Combine(
        FileSystem.AppDataDirectory,
        "Documents");

            Directory.CreateDirectory(documentsPath);

            var destinationPath = Path.Combine(
                documentsPath,
                result.FileName);

            await using var source = await result.OpenReadAsync();
            await using var destination = File.Create(destinationPath);

            await source.CopyToAsync(destination);

            var document = new TripDocumentDTO
            {
                Id = Guid.NewGuid(),
                //TripId = _trip.Id,
                FileName = result.FileName,
                LocalPath = destinationPath,
                AddedAt = DateTime.UtcNow,
                FileSize = fileInfo.Length
            };

            await _tripRepository.SaveDocAsync(document);

            Docs.Add(new TripDocument
            {
                Id = document.Id,
                //TripId = document.TripId,
                FileName = document.FileName,
                LocalPath = document.LocalPath,
                AddedAt = document.AddedAt,
                FileSize = document.FileSize
            });
        }

        private static string T(string key) => LocalizationResourceManager.Instance[key];

        private async Task LoadDocumentsAsync()
        {
            var documents = await _tripRepository.GetByTripDocsAsync();

            var existingIds = Docs
                .Select(x => x.Id)
                .ToHashSet();

            foreach (var document in documents)
            {
                if (!existingIds.Add(document.Id))
                    continue;

                Docs.Add(new TripDocument
                {
                    Id = document.Id,
                    TripId = document.TripId,
                    FileName = document.FileName,
                    LocalPath = document.LocalPath,
                    AddedAt = document.AddedAt
                });
            }
        }

        public async Task OnAppearingAsync()
        {
            var destinations = await _tripRepository.GetActiveTripsWithStatsAsync();

            //var docs = await _tripRepository.GetTripDocumentsAsync(_localTripId);
            //Docs.ReplaceRange(docs);

            await LoadDocumentsAsync();
        }

        //protected override async Task OnNavigatedToAsync(IDictionary<string, object> query)
        //{
        //    if (query.TryGetValue("localTripId", out var localTripIdObj))
        //    {
        //        _localTripId = Convert.ToInt32(localTripIdObj);

        //    }
        //}
    }
}

using Packo.Services;

namespace Packo.Views;

public partial class StartupPage : ContentPage
{
    private const string LoadingAnimation = "StartupLoading";
    private bool _isLoading = true;

    public event EventHandler? RetryRequested;

    public StartupPage()
    {
        InitializeComponent();
    }

    private void StartLoadingAnimation()
    {
        this.AbortAnimation(LoadingAnimation);
        if (!_isLoading || !LoadingTrack.IsLoaded || LoadingTrack.Width <= 0)
            return;

        // Indeterminate progress: network response time does not imply a percentage.
        var segmentWidth = LoadingSegment.WidthRequest;
        LoadingSegment.TranslationX = -segmentWidth;
        new Animation(value => LoadingSegment.TranslationX = value,
            -segmentWidth, LoadingTrack.Width)
            .Commit(this, LoadingAnimation, length: 1200, easing: Easing.Linear,
                repeat: () => _isLoading && LoadingTrack.IsLoaded);
    }

    private void OnLoadingTrackLoaded(object? sender, EventArgs e) => StartLoadingAnimation();
    private void OnLoadingTrackSizeChanged(object? sender, EventArgs e) => StartLoadingAnimation();
    private void OnLoadingTrackUnloaded(object? sender, EventArgs e) => this.AbortAnimation(LoadingAnimation);

    public void ShowLoading(string statusKey)
    {
        StatusLabel.Text = LocalizationResourceManager.Instance[statusKey];
        _isLoading = true;
        LoadingTrack.IsVisible = true;
        RetryButton.IsVisible = false;
        StartLoadingAnimation();
    }

    public void ShowError()
    {
        _isLoading = false;
        this.AbortAnimation(LoadingAnimation);
        LoadingTrack.IsVisible = false;
        StatusLabel.Text = LocalizationResourceManager.Instance["StartupLoadError"];
        RetryButton.IsVisible = true;
    }

    private void OnRetryClicked(object? sender, EventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);

    protected override bool OnBackButtonPressed() => true;
}

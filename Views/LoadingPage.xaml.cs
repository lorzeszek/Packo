using Packo.Services;

namespace Packo.Views;

public partial class LoadingPage : ContentPage
{
    public LoadingPage()
    {
        InitializeComponent();
    }

    public LoadingPage(string statusKey) : this()
    {
        StatusLabel.Text = LocalizationResourceManager.Instance[statusKey];
        _blockBack = true;
    }

    private readonly bool _blockBack;

    protected override bool OnBackButtonPressed() => _blockBack || base.OnBackButtonPressed();
}

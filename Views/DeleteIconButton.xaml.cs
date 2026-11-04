using System.Windows.Input;

namespace Packo.Views;

public partial class DeleteIconButton : ContentView
{
    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command), typeof(ICommand), typeof(DeleteIconButton));

    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(
        nameof(CommandParameter), typeof(object), typeof(DeleteIconButton));

    public static readonly BindableProperty ActionDescriptionProperty = BindableProperty.Create(
        nameof(ActionDescription), typeof(string), typeof(DeleteIconButton), string.Empty);

    public static readonly BindableProperty SurfaceColorProperty = BindableProperty.Create(
        nameof(SurfaceColor), typeof(Color), typeof(DeleteIconButton), Color.FromArgb("#FFFFFFE8"));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public string ActionDescription
    {
        get => (string)GetValue(ActionDescriptionProperty);
        set => SetValue(ActionDescriptionProperty, value);
    }

    public Color SurfaceColor
    {
        get => (Color)GetValue(SurfaceColorProperty);
        set => SetValue(SurfaceColorProperty, value);
    }

    public DeleteIconButton() => InitializeComponent();
}

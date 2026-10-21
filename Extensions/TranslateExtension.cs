using Microsoft.Maui.Controls.Xaml;
using Packo.Services;

namespace Packo.Extensions;

[ContentProperty(nameof(Key))]
public sealed class TranslateExtension : IMarkupExtension<BindingBase>
{
    public string Key { get; set; } = string.Empty;

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]", source: LocalizationResourceManager.Instance, mode: BindingMode.OneWay);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}

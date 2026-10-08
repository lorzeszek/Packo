using System.Globalization;

namespace Packo.Converters
{
    public class BoolToOpacityConverter : IValueConverter
    {
        public double TrueOpacity { get; set; } = 1.0;   // domyślnie w pełni widoczne
        public double FalseOpacity { get; set; } = 0.5;  // przygaszone po wycieczce

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isActive)
                return isActive ? TrueOpacity : FalseOpacity;

            return TrueOpacity;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

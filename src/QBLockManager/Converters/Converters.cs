using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using QBLockManager.Models;

namespace QBLockManager.Converters;

public class AvailabilityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FileAvailability av)
        {
            return av switch
            {
                FileAvailability.Available => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                FileAvailability.LockedByMe => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                FileAvailability.LockedByOther => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                FileAvailability.Stale => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                _ => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF))
            };
        }
        if (value is bool b)
        {
            // ServiceReachable
            return b
                ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))
                : new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool result = parameter?.ToString() == "notnull" ? value != null : value is true;
        return ReturnAs(result, targetType);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true || value is Visibility.Visible;

    private static object ReturnAs(bool result, Type targetType)
    {
        if (targetType == typeof(bool) || targetType == typeof(bool?))
            return result;
        return result ? Visibility.Visible : Visibility.Collapsed;
    }
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool result = value is not true;
        if (targetType == typeof(bool) || targetType == typeof(bool?))
            return result;
        return result ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true && value is not Visibility.Visible;
}

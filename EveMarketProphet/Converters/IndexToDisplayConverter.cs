using System;
using System.Globalization;
using System.Windows.Data;

namespace EveMarketProphet.Converters
{
    public class IndexToDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int index)
            {
                return (index + 1).ToString(culture);
            }

            return "1";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}

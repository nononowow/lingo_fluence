using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LingoFluence.Converters;

/// <summary>Converts bool → Visibility (True=Visible, False=Collapsed).</summary>
public class BoolToVisConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is true ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is Visibility.Visible ^ Invert;
}

/// <summary>Converts null → Collapsed, non-null → Visible.</summary>
public class NullToVisConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c)
        => (v == null) ^ Invert ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Converts int 0 → Collapsed, non-zero → Visible.</summary>
public class ZeroToCollapsedConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

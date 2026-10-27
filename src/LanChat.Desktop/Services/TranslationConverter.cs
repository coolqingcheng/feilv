using System.Globalization;
using Avalonia.Data.Converters;
using LanChat.Core.Localization;

namespace LanChat.Desktop.Services;

/// <summary>Presentation boundary for invariant backend state and status messages; never bind user content through it.</summary>
public sealed class TranslationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Localizer.Translate(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

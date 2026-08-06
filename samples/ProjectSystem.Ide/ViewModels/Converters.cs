using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ProjectSystem.Ide.ViewModels;

/// <summary>Just enough colour to make an error findable.</summary>
public static class Converters
{
    private static readonly IBrush Error = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
    private static readonly IBrush Normal = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));

    /// <summary>Red when the bound boolean says something failed.</summary>
    public static IValueConverter ErrorBrush { get; } = new FuncValueConverter<bool, IBrush>(
        failed => failed ? Error : Normal);

    /// <summary>Red, amber or plain, from a severity's name.</summary>
    public static IValueConverter SeverityBrush { get; } = new FuncValueConverter<string?, IBrush>(
        severity => severity switch
        {
            "Error" => Error,
            "Warning" => Warning,
            _ => Normal,
        });

    private sealed class FuncValueConverter<TIn, TOut>(Func<TIn, TOut> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is TIn typed ? convert(typed) : convert(default!);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException("These are for display only.");
    }
}

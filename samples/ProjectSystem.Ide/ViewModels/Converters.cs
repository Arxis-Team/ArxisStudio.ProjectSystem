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

    private static readonly IBrush CSharp = new SolidColorBrush(Color.FromRgb(0x6C, 0xC6, 0x44));
    private static readonly IBrush Markup = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly IBrush Project = new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0));
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>
    /// The little colour that makes a file list scannable, keyed off what the file is.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow: an icon set would be a better answer and a worse sample, because it
    /// would be the part of this window somebody had to maintain.
    /// </remarks>
    public static IValueConverter NodeBrush { get; } = new FuncValueConverter<TreeNode?, IBrush>(
        node => node?.Kind switch
        {
            TreeNodeKind.Solution or TreeNodeKind.Project => Project,
            TreeNodeKind.File => node.Extension switch
            {
                ".cs" => CSharp,
                ".axaml" or ".xaml" => Markup,
                ".csproj" or ".fsproj" or ".vbproj" => Project,
                _ => Muted,
            },
            _ => Muted,
        });

    private sealed class FuncValueConverter<TIn, TOut>(Func<TIn, TOut> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is TIn typed ? convert(typed) : convert(default!);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException("These are for display only.");
    }
}

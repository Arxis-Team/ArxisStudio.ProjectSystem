using System;
using System.Globalization;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A range inside a text file, for a diagnostic that is more precise than the whole file.
/// </summary>
/// <remarks>
/// <para>
/// Lines and columns are <b>one-based</b>, because MSBuild reports them that way and MSBuild is
/// what will populate them. Making the core agree with its future provider costs nothing now and
/// avoids an off-by-one that would otherwise be converted at every boundary.
/// </para>
/// <para>
/// Zero therefore means "not specified", which makes <see langword="default"/> the natural absent
/// value — <see cref="None"/>.
/// </para>
/// </remarks>
public readonly record struct FileSpan
{
    /// <summary>Gets the absent span, which is also <see langword="default"/>.</summary>
    public static FileSpan None => default;

    /// <summary>Gets the one-based first line, or zero when unspecified.</summary>
    public int StartLine { get; init; }

    /// <summary>Gets the one-based first column, or zero when unspecified.</summary>
    public int StartColumn { get; init; }

    /// <summary>Gets the one-based last line, or zero when unspecified.</summary>
    public int EndLine { get; init; }

    /// <summary>Gets the one-based last column, or zero when unspecified.</summary>
    public int EndColumn { get; init; }

    /// <summary>Gets a value indicating whether any position is specified.</summary>
    public bool IsEmpty => StartLine <= 0;

    /// <summary>Creates a span naming a single position.</summary>
    /// <param name="line">The one-based line.</param>
    /// <param name="column">The one-based column, or zero when only the line is known.</param>
    /// <returns>The span.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="line"/> or <paramref name="column"/> is negative.</exception>
    public static FileSpan At(int line, int column = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfNegative(column);

        return new FileSpan
        {
            StartLine = line,
            StartColumn = column,
            EndLine = line,
            EndColumn = column,
        };
    }

    /// <summary>Creates a span across a range.</summary>
    /// <param name="startLine">The one-based first line.</param>
    /// <param name="startColumn">The one-based first column.</param>
    /// <param name="endLine">The one-based last line.</param>
    /// <param name="endColumn">The one-based last column.</param>
    /// <returns>The span.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is negative.</exception>
    /// <exception cref="ArgumentException">The end precedes the start.</exception>
    public static FileSpan Create(int startLine, int startColumn, int endLine, int endColumn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startLine);
        ArgumentOutOfRangeException.ThrowIfNegative(startColumn);
        ArgumentOutOfRangeException.ThrowIfNegative(endLine);
        ArgumentOutOfRangeException.ThrowIfNegative(endColumn);

        if (endLine < startLine || (endLine == startLine && endColumn != 0 && endColumn < startColumn))
        {
            throw new ArgumentException(
                $"The span ({startLine},{startColumn})-({endLine},{endColumn}) ends before it starts.",
                nameof(endLine));
        }

        return new FileSpan
        {
            StartLine = startLine,
            StartColumn = startColumn,
            EndLine = endLine,
            EndColumn = endColumn,
        };
    }

    /// <summary>Returns the span in a readable form.</summary>
    /// <returns>Something like <c>(12,5)</c> or <c>(12,5)-(14,9)</c>.</returns>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return "(none)";
        }

        string start = string.Create(CultureInfo.InvariantCulture, $"({StartLine},{StartColumn})");

        return StartLine == EndLine && StartColumn == EndColumn
            ? start
            : start + string.Create(CultureInfo.InvariantCulture, $"-({EndLine},{EndColumn})");
    }
}

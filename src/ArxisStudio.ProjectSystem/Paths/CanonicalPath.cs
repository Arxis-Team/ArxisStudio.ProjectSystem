using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// An absolute, normalised file-system path, compared by one documented policy.
/// </summary>
/// <remarks>
/// <para>
/// A project system is mostly paths, and the bug this type exists to prevent is one project
/// appearing twice because a solution spelled its path differently from a project reference. Every
/// path in the model is a <see cref="CanonicalPath"/>; a <see cref="string"/> in the model is a
/// display name, never a path.
/// </para>
/// <para>
/// <b>Comparison is case-insensitive on every operating system.</b> The reasoning is recorded on
/// <c>CanonicalPathFormat.Comparer</c>: the domain is MSBuild, which compares this way everywhere,
/// and a core that split what the engine considers one project would be wrong about its own data.
/// Casing is preserved in <see cref="Value"/>; only comparison folds it.
/// </para>
/// <para>
/// Nothing here touches the file system. Normalisation is lexical, and a path naming a file that
/// does not exist is a perfectly valid <see cref="CanonicalPath"/> — noticing that it is missing
/// is a provider's job. In particular, existence is never consulted by equality or hashing, which
/// would otherwise make a snapshot's meaning depend on the state of the disk at the moment two
/// values happened to be compared.
/// </para>
/// <para>
/// The absent value is <see langword="default"/>, which is <see cref="None"/>. There is
/// deliberately no nullable form in the public API: <c>CanonicalPath?</c> would give absence two
/// spellings — <see langword="null"/>, and a non-null value that is itself <see cref="None"/> —
/// and every consumer eventually checks only one of them.
/// </para>
/// </remarks>
public readonly struct CanonicalPath : IEquatable<CanonicalPath>, IComparable<CanonicalPath>
{
    private readonly string? _value;

    private CanonicalPath(string value) => _value = value;

    /// <summary>Gets the absent path, which is also <see langword="default"/>.</summary>
    public static CanonicalPath None => default;

    /// <summary>Gets the canonical text of the path, or an empty string when absent.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Gets a value indicating whether this is <see cref="None"/>.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Gets the file or directory name, without any directory part.</summary>
    public string FileName => IsEmpty ? string.Empty : Path.GetFileName(Value);

    /// <summary>Gets the extension including its leading dot, or an empty string when there is none.</summary>
    public string Extension => IsEmpty ? string.Empty : Path.GetExtension(Value);

    /// <summary>Gets the containing directory, or <see cref="None"/> at a root.</summary>
    public CanonicalPath Directory
    {
        get
        {
            if (IsEmpty)
            {
                return None;
            }

            string? parent = Path.GetDirectoryName(Value);

            return string.IsNullOrEmpty(parent) ? None : new CanonicalPath(parent);
        }
    }

    /// <summary>Determines whether two paths name the same file.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when they are equal under the comparison policy.</returns>
    public static bool operator ==(CanonicalPath left, CanonicalPath right) => left.Equals(right);

    /// <summary>Determines whether two paths name different files.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when they are not equal.</returns>
    public static bool operator !=(CanonicalPath left, CanonicalPath right) => !left.Equals(right);

    /// <summary>Determines whether one path sorts before another.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts first.</returns>
    public static bool operator <(CanonicalPath left, CanonicalPath right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether one path sorts before or equal to another.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not sort after.</returns>
    public static bool operator <=(CanonicalPath left, CanonicalPath right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether one path sorts after another.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts last.</returns>
    public static bool operator >(CanonicalPath left, CanonicalPath right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether one path sorts after or equal to another.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not sort before.</returns>
    public static bool operator >=(CanonicalPath left, CanonicalPath right) => left.CompareTo(right) >= 0;

    /// <summary>Creates a canonical path from an absolute path.</summary>
    /// <param name="absolutePath">The path to canonicalise.</param>
    /// <returns>The canonical form.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="absolutePath"/> is null, blank, contains a null character, is not fully
    /// qualified, or is otherwise not a usable path.
    /// </exception>
    public static CanonicalPath Create(string absolutePath)
    {
        if (!CanonicalPathFormat.TryNormalize(absolutePath, out string normalised, out string? error))
        {
            throw new ArgumentException(error, nameof(absolutePath));
        }

        return new CanonicalPath(normalised);
    }

    /// <summary>Creates a canonical path by resolving a relative path against a directory.</summary>
    /// <param name="baseDirectory">The directory to resolve against.</param>
    /// <param name="relativePath">
    /// The path to resolve. An absolute path is accepted and replaces the base entirely, which is
    /// the behaviour a project file's <c>Include</c> needs.
    /// </param>
    /// <returns>The canonical form.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseDirectory"/> is <see cref="None"/>, or <paramref name="relativePath"/>
    /// is null, blank, contains a null character, or is otherwise not a usable path.
    /// </exception>
    public static CanonicalPath Create(CanonicalPath baseDirectory, string relativePath)
    {
        if (baseDirectory.IsEmpty)
        {
            throw new ArgumentException(
                "A relative path cannot be resolved against an absent base directory.",
                nameof(baseDirectory));
        }

        if (!CanonicalPathFormat.TryNormalize(
            baseDirectory.Value, relativePath, out string normalised, out string? error))
        {
            throw new ArgumentException(error, nameof(relativePath));
        }

        return new CanonicalPath(normalised);
    }

    /// <summary>Attempts to create a canonical path, for a caller that wants a diagnostic instead of an exception.</summary>
    /// <param name="absolutePath">The path to canonicalise.</param>
    /// <param name="path">The canonical form, when the input is acceptable.</param>
    /// <returns><see langword="true"/> when the path was canonicalised.</returns>
    public static bool TryCreate(string? absolutePath, out CanonicalPath path)
    {
        if (CanonicalPathFormat.TryNormalize(absolutePath, out string normalised, out _))
        {
            path = new CanonicalPath(normalised);

            return true;
        }

        path = None;

        return false;
    }

    /// <summary>Resolves a relative path against this one.</summary>
    /// <param name="relativePath">The path to resolve.</param>
    /// <returns>The combined canonical path.</returns>
    /// <exception cref="ArgumentException">This path is <see cref="None"/>, or the argument is unusable.</exception>
    public CanonicalPath Combine(string relativePath) => Create(this, relativePath);

    /// <summary>
    /// Determines whether this path is inside a directory.
    /// </summary>
    /// <remarks>
    /// Segment-aware, so <c>C:\src\App</c> does not contain <c>C:\src\AppOther</c> — a plain string
    /// prefix test would say it does. A directory contains itself.
    /// </remarks>
    /// <param name="directory">The directory to test against.</param>
    /// <returns><see langword="true"/> when this path is at or below <paramref name="directory"/>.</returns>
    public bool StartsWith(CanonicalPath directory)
    {
        if (IsEmpty || directory.IsEmpty)
        {
            return false;
        }

        string self = Value;
        string other = directory.Value;

        if (!self.StartsWith(other, CanonicalPathFormat.Comparison))
        {
            return false;
        }

        // Equal, or the next character begins a new segment. The second test also covers a root
        // such as "C:\", which already ends in a separator and has no boundary character to spare.
        return self.Length == other.Length
            || other.EndsWith(Path.DirectorySeparatorChar)
            || self[other.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>Determines whether this path equals another.</summary>
    /// <param name="other">The path to compare with.</param>
    /// <returns><see langword="true"/> when they are equal under the comparison policy.</returns>
    public bool Equals(CanonicalPath other) => CanonicalPathFormat.Comparer.Equals(Value, other.Value);

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is CanonicalPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => CanonicalPathFormat.Comparer.GetHashCode(Value);

    /// <summary>Compares this path with another for ordering.</summary>
    /// <param name="other">The path to compare with.</param>
    /// <returns>A negative value, zero, or a positive value.</returns>
    public int CompareTo(CanonicalPath other) => CanonicalPathFormat.Comparer.Compare(Value, other.Value);

    /// <summary>Returns the canonical text of the path.</summary>
    /// <returns>The path, or an empty string when absent.</returns>
    public override string ToString() => Value;
}

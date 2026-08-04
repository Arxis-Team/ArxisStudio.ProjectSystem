using System;
using System.Diagnostics.CodeAnalysis;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// What the user asked the system to open, classified without opening it.
/// </summary>
/// <remarks>
/// <para>
/// The core knows the neutral distinction between a solution, a solution XML file and a project,
/// and knows it from the extension alone. It does not parse any of those formats — that is a
/// provider's job, and in this milestone no provider exists.
/// </para>
/// <para>
/// A <see cref="WorkspaceEntryPointKind.Unknown"/> kind is not a rejection. Whether something can
/// be opened is the provider's judgement, and a core that pre-judged it would need editing every
/// time a new project type appeared.
/// </para>
/// </remarks>
public readonly struct WorkspaceEntryPoint : IEquatable<WorkspaceEntryPoint>
{
    private WorkspaceEntryPoint(CanonicalPath path, WorkspaceEntryPointKind kind)
    {
        Path = path;
        Kind = kind;
    }

    /// <summary>Gets the absent entry point, which is also <see langword="default"/>.</summary>
    public static WorkspaceEntryPoint None => default;

    /// <summary>Gets the canonical path of the file to open.</summary>
    public CanonicalPath Path { get; }

    /// <summary>Gets what kind of file it appears to be.</summary>
    public WorkspaceEntryPointKind Kind { get; }

    /// <summary>Gets the file's extension including its leading dot.</summary>
    public string Extension => Path.Extension;

    /// <summary>Gets a value indicating whether this is <see cref="None"/>.</summary>
    public bool IsEmpty => Path.IsEmpty;

    /// <summary>Determines whether two entry points are the same.</summary>
    /// <param name="left">The first entry point.</param>
    /// <param name="right">The second entry point.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(WorkspaceEntryPoint left, WorkspaceEntryPoint right) => left.Equals(right);

    /// <summary>Determines whether two entry points differ.</summary>
    /// <param name="left">The first entry point.</param>
    /// <param name="right">The second entry point.</param>
    /// <returns><see langword="true"/> when they are not equal.</returns>
    public static bool operator !=(WorkspaceEntryPoint left, WorkspaceEntryPoint right) => !left.Equals(right);

    /// <summary>Classifies a path as an entry point.</summary>
    /// <param name="path">The canonical path of the file to open.</param>
    /// <returns>The entry point, or <see cref="None"/> when the path is absent.</returns>
    public static WorkspaceEntryPoint FromPath(CanonicalPath path) =>
        path.IsEmpty ? None : new WorkspaceEntryPoint(path, Classify(path.Extension));

    /// <summary>Determines whether this entry point equals another.</summary>
    /// <param name="other">The entry point to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public bool Equals(WorkspaceEntryPoint other) => Path == other.Path && Kind == other.Kind;

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is WorkspaceEntryPoint other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Path, Kind);

    /// <summary>Returns the kind and the path.</summary>
    /// <returns>Something like <c>Solution: C:\src\App.sln</c>.</returns>
    public override string ToString() => IsEmpty ? "(none)" : $"{Kind}: {Path}";

    /// <summary>
    /// Classifies by extension.
    /// </summary>
    /// <remarks>
    /// The project rule is "ends in <c>proj</c>", which is MSBuild's own convention and covers
    /// <c>.csproj</c>, <c>.fsproj</c>, <c>.vbproj</c>, <c>.vcxproj</c> and whatever comes next
    /// without this switch having to learn about it.
    /// </remarks>
    private static WorkspaceEntryPointKind Classify(string extension) => extension switch
    {
        _ when extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) => WorkspaceEntryPointKind.Solution,
        _ when extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) => WorkspaceEntryPointKind.SolutionXml,
        _ when extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase) => WorkspaceEntryPointKind.Project,
        _ => WorkspaceEntryPointKind.Unknown,
    };
}

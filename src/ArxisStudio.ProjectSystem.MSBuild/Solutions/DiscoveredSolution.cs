using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// What reading a solution file found, in a shape that owes nothing to any parser.
/// </summary>
/// <remarks>
/// The same seam as <see cref="EvaluatedProject"/> and for the same reason: a test builds one of
/// these by hand, so the rules about turning a solution into a snapshot are cheap to state and
/// cheap to check. The reader that produces it is thin and is covered by a few integration tests
/// against real solution files.
/// </remarks>
internal sealed record DiscoveredSolution
{
    /// <summary>Gets the canonical path of the solution file.</summary>
    public required CanonicalPath FullPath { get; init; }

    /// <summary>Gets the solution's display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the projects the solution lists, in its own order.</summary>
    public ImmutableArray<DiscoveredProject> Projects
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets the display folders the solution declares.</summary>
    public ImmutableArray<DiscoveredFolder> Folders
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets the configurations the solution declares.</summary>
    public ImmutableArray<string> Configurations
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets the platforms the solution declares.</summary>
    public ImmutableArray<string> Platforms
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];
}

/// <summary>One project listed in a solution.</summary>
internal sealed record DiscoveredProject
{
    /// <summary>Gets the canonical path of the project file.</summary>
    public required CanonicalPath FullPath { get; init; }

    /// <summary>Gets the name the solution displays for it, which need not match the file name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the path of the folder it sits in, or <see langword="null"/> at the root.</summary>
    public string? FolderPath { get; init; }
}

/// <summary>One display folder in a solution.</summary>
internal sealed record DiscoveredFolder
{
    /// <summary>Gets the folder's display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets its slash-separated position in the tree, such as <c>/src/Libraries/</c>.</summary>
    public required string Path { get; init; }
}

using System;
using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A request to restore, build, rebuild or clean, in terms no provider-specific type appears in.
/// </summary>
/// <remarks>
/// <para>
/// Shaped like <see cref="WorkspaceLoadRequest"/> on purpose: the same entry point, the same
/// configuration choices, the same neutral property bag. An operation and a load are asking about
/// the same thing in the same context, and two vocabularies for that would be one too many.
/// </para>
/// <para>
/// <see cref="Projects"/> narrows the work. Empty means the whole entry point, which for a solution
/// is every project it lists.
/// </para>
/// </remarks>
public sealed record ProjectOperationRequest
{
    /// <summary>Gets what to do.</summary>
    public required ProjectOperationKind Kind { get; init; }

    /// <summary>Gets the workspace this operation belongs to.</summary>
    public required WorkspaceIdentity Workspace
    {
        get => field;
        init => field = value.IsEmpty
            ? throw new ArgumentException(
                "An operation needs the identity of the workspace it belongs to.", nameof(value))
            : value;
    }

    /// <summary>Gets the solution or project to operate on.</summary>
    public required CanonicalPath EntryPointPath
    {
        get => field;
        init => field = value.IsEmpty
            ? throw new ArgumentException("An operation needs something to operate on.", nameof(value))
            : value;
    }

    /// <summary>Gets the entry point, classified from <see cref="EntryPointPath"/>.</summary>
    public WorkspaceEntryPoint EntryPoint => WorkspaceEntryPoint.FromPath(EntryPointPath);

    /// <summary>
    /// Gets the projects to operate on, or empty for everything the entry point covers.
    /// </summary>
    public ImmutableArray<ProjectIdentity> Projects
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets the configuration to operate under, or <see langword="null"/> for the provider's default.</summary>
    public string? Configuration { get; init; }

    /// <summary>Gets the platform to operate under, or <see langword="null"/> for the provider's default.</summary>
    public string? Platform { get; init; }

    /// <summary>Gets the target framework to operate under, or <see langword="null"/> for all of them.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Gets properties a provider should operate with. Keys compare case-insensitively.</summary>
    public ProjectMetadata GlobalProperties { get; init; } = ProjectMetadata.Empty;

    /// <summary>Returns the kind and the entry point.</summary>
    /// <returns>Something like <c>Build C:\src\App.sln</c>.</returns>
    public override string ToString() => $"{Kind} {EntryPointPath}";
}

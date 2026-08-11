namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A shared-framework reference, such as <c>Microsoft.AspNetCore.App</c>.
/// </summary>
/// <remarks>
/// The name is a framework identifier, not a path and not an assembly name. Resolving it to files
/// on disk needs an installed runtime and is a provider's job.
/// </remarks>
public sealed record FrameworkReferenceInfo
{
    /// <summary>Gets the framework identifier.</summary>
    public required string Name { get; init; }


    /// <summary>Gets any additional declared metadata.</summary>
    public ProjectMetadata Metadata { get; init; } = ProjectMetadata.Empty;

    /// <summary>Returns the framework name.</summary>
    /// <returns>Something like <c>FrameworkReference: Microsoft.AspNetCore.App</c>.</returns>
    public override string ToString() => $"FrameworkReference: {Name}";
}

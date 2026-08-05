namespace ArxisStudio.ProjectSystem;

/// <summary>Where an assembly a project needs at run time came from.</summary>
public enum RuntimeAssemblyOrigin
{
    /// <summary>The project's own build output.</summary>
    Project = 0,

    /// <summary>The output of a project this one references, directly or through another.</summary>
    ProjectReference = 1,

    /// <summary>A file contributed by a restored package.</summary>
    Package = 2,
}

/// <summary>
/// One assembly a project needs at run time, and why.
/// </summary>
/// <remarks>
/// A path and its provenance. Nothing here loads the file, checks that it exists, or has an opinion
/// about which <c>AssemblyLoadContext</c> it belongs in — those are the host's, and keeping them out
/// of the model is what lets the model be read after everything that produced it is gone.
/// </remarks>
public sealed record RuntimeAssemblyReference
{
    /// <summary>Gets where the assembly is.</summary>
    public required CanonicalPath Path { get; init; }

    /// <summary>Gets what contributed it.</summary>
    public required RuntimeAssemblyOrigin Origin { get; init; }

    /// <summary>
    /// Gets the project that produced it, when <see cref="Origin"/> is a project or a project
    /// reference.
    /// </summary>
    public ProjectIdentity Project { get; init; }

    /// <summary>Gets the package that contributed it, when <see cref="Origin"/> is a package.</summary>
    public string? PackageId { get; init; }

    /// <summary>Returns the file and where it came from.</summary>
    /// <returns>Something like <c>Package(Serilog): C:\...\Serilog.dll</c>.</returns>
    public override string ToString() =>
        PackageId is null ? $"{Origin}: {Path}" : $"{Origin}({PackageId}): {Path}";
}

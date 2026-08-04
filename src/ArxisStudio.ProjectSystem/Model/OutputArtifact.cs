namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Something a build produced, described by where it is and what it is.
/// </summary>
/// <remarks>
/// <para>
/// A path and a kind, and nothing else. This type does not load the assembly it names, does not
/// own an <c>AssemblyLoadContext</c>, and does not check that the file exists. A consumer that
/// wants to load an output is welcome to, and owns the lifetime problem that comes with it — which
/// is precisely why the snapshot does not.
/// </para>
/// <para>
/// <see cref="TargetFramework"/> matters more than it looks: a cross-targeting project produces one
/// of most of these per framework, so the path alone does not identify which build it came from.
/// </para>
/// </remarks>
public sealed record OutputArtifact
{
    /// <summary>Gets what this artifact is.</summary>
    public required OutputArtifactKind Kind { get; init; }

    /// <summary>Gets where the artifact is.</summary>
    public required CanonicalPath Path { get; init; }

    /// <summary>Gets the target framework whose build produced it, when the project cross-targets.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Gets any additional provider-supplied detail.</summary>
    public ProjectMetadata Metadata { get; init; } = ProjectMetadata.Empty;

    /// <summary>Returns the kind and the path.</summary>
    /// <returns>Something like <c>Assembly: C:\src\App\bin\App.dll</c>.</returns>
    public override string ToString() => $"{Kind}: {Path}";
}

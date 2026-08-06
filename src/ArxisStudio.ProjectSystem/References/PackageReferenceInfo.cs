namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A NuGet package reference, as the project declares it.
/// </summary>
/// <remarks>
/// <para>
/// The version is <see cref="VersionText"/> and not <c>Version</c>, and it is a
/// <see cref="string"/>. That is deliberate to the point of being the reason this property is
/// named the way it is: the core does not parse NuGet version ranges, does not resolve them, and
/// does not compare them. <c>[1.0,2.0)</c>, <c>1.2.*</c> and <c>$(SomeProperty)</c> are all things
/// a project file can say here, and interpreting any of them is NuGet's job.
/// </para>
/// <para>
/// This is a <em>declared</em> reference. What restore actually resolved is a different question,
/// answered by reading restore assets — Milestone 3.
/// </para>
/// </remarks>
public sealed record PackageReferenceInfo
{
    /// <summary>Gets the package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>Gets the version as written, uninterpreted, or <see langword="null"/> when the project does not say.</summary>
    public string? VersionText { get; init; }

    /// <summary>Gets the declared <c>PrivateAssets</c>, uninterpreted.</summary>
    public string? PrivateAssets { get; init; }

    /// <summary>Gets the declared <c>IncludeAssets</c>, uninterpreted.</summary>
    public string? IncludeAssets { get; init; }

    /// <summary>Gets the declared <c>ExcludeAssets</c>, uninterpreted.</summary>
    public string? ExcludeAssets { get; init; }

    /// <summary>Gets the MSBuild condition guarding the reference, when it has one.</summary>
    public string? Condition { get; init; }

    /// <summary>Gets any additional declared metadata.</summary>
    public ProjectMetadata Metadata { get; init; } = ProjectMetadata.Empty;

    /// <summary>Gets whether the project file itself declares this, or an import does.</summary>
    /// <remarks>
    /// <para>
    /// A project's evaluated package references are a superset of what is written in its own file:
    /// a <c>Directory.Build.props</c>, a <c>GlobalPackageReference</c> or an SDK can each contribute
    /// one, and the evaluated result does not otherwise say which. That matters to anything that
    /// intends to <em>edit</em> the reference, because an editor rewrites one file and a reference
    /// it did not write is not in the file it is about to open.
    /// </para>
    /// <para>
    /// This is on package references and not yet on the other kinds because this is the kind the
    /// family edits. A provider that cannot tell leaves it <see cref="ProjectItemOrigin.Unknown"/>,
    /// which is deliberately not the same answer as <see cref="ProjectItemOrigin.Imported"/>.
    /// </para>
    /// </remarks>
    public ProjectItemOrigin Origin { get; init; }

    /// <summary>Returns the package id and version text.</summary>
    /// <returns>Something like <c>PackageReference: Serilog 4.1.0</c>.</returns>
    public override string ToString() =>
        VersionText is null ? $"PackageReference: {PackageId}" : $"PackageReference: {PackageId} {VersionText}";
}

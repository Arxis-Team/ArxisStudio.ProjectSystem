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

    /// <summary>Returns the package id and version text.</summary>
    /// <returns>Something like <c>PackageReference: Serilog 4.1.0</c>.</returns>
    public override string ToString() =>
        VersionText is null ? $"PackageReference: {PackageId}" : $"PackageReference: {PackageId} {VersionText}";
}

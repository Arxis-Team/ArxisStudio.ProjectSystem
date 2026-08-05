namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Which MSBuild this process is using, and where it came from.
/// </summary>
/// <remarks>
/// Deliberately not the locator's own <c>VisualStudioInstance</c>. That type belongs to
/// <c>Microsoft.Build.Locator</c>, and letting it out here would put an MSBuild assembly in the
/// public surface of a package whose whole purpose is to keep MSBuild off the consumer's side of
/// the boundary. An architecture test enforces the rule; this record is what satisfies it.
/// </remarks>
public sealed record MSBuildRegistration
{
    /// <summary>Gets the name of the installation, such as <c>.NET SDK</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets its version, as the locator reported it.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the directory the MSBuild assemblies were loaded from.</summary>
    public required CanonicalPath Path { get; init; }

    /// <summary>Returns the name, version and path.</summary>
    /// <returns>Something like <c>.NET SDK 10.0.301 (C:\Program Files\dotnet\sdk\10.0.301)</c>.</returns>
    public override string ToString() => $"{Name} {Version} ({Path})";
}

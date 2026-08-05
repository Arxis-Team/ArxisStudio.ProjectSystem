using System;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>
/// Where a project's package versions are written.
/// </summary>
/// <remarks>
/// <para>
/// The one thing an editor cannot guess. Under central package management the project declares
/// <c>&lt;PackageReference Include="X" /&gt;</c> with no version at all and the version lives in
/// <c>Directory.Packages.props</c>; without it the version is an attribute on the reference itself.
/// Writing a version in the wrong place produces a file that either does not restore or silently
/// keeps the old version.
/// </para>
/// <para>
/// Both facts come out of an evaluation rather than out of a filename: <c>ManagePackageVersionsCentrally</c>
/// says whether it is on, and <c>DirectoryPackagesPropsPath</c> says exactly which file it is —
/// which need not be beside the project, and need not be called that. <see cref="From"/> reads them
/// off a snapshot, so a caller that already loaded the project does not have to know either name.
/// </para>
/// </remarks>
public sealed record PackageVersionLayout
{
    /// <summary>Gets the layout for a project that carries its own versions.</summary>
    public static PackageVersionLayout Beside { get; } = new();

    /// <summary>Gets a value indicating whether versions live in a central file.</summary>
    public bool IsCentral => !VersionsFilePath.IsEmpty;

    /// <summary>
    /// Gets the file holding the versions, or <see cref="CanonicalPath.None"/> when each project
    /// carries its own.
    /// </summary>
    public CanonicalPath VersionsFilePath { get; init; }

    /// <summary>Describes a project whose versions live centrally.</summary>
    /// <param name="versionsFilePath">The file holding them.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentException"><paramref name="versionsFilePath"/> is empty.</exception>
    public static PackageVersionLayout Central(CanonicalPath versionsFilePath)
    {
        if (versionsFilePath.IsEmpty)
        {
            throw new ArgumentException(
                "Central package management needs a file to hold the versions.", nameof(versionsFilePath));
        }

        return new PackageVersionLayout { VersionsFilePath = versionsFilePath };
    }

    /// <summary>
    /// Works the layout out from a loaded project.
    /// </summary>
    /// <remarks>
    /// Falls back to <see cref="Beside"/> when the project says nothing, which is the right answer
    /// for a project that is not centrally managed and the safe one for a provider that did not
    /// surface the properties: writing a version onto the reference is what an ordinary project
    /// expects, and NuGet reports the mistake plainly if it turns out to be wrong.
    /// </remarks>
    /// <param name="project">The project to read.</param>
    /// <returns>Its layout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="project"/> is <see langword="null"/>.</exception>
    public static PackageVersionLayout From(ProjectSnapshot project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!project.Properties.TryGetValue("ManagePackageVersionsCentrally", out string? central)
            || !bool.TryParse(central, out bool isCentral)
            || !isCentral)
        {
            return Beside;
        }

        return project.Properties.TryGetValue("DirectoryPackagesPropsPath", out string? path)
            && CanonicalPath.TryCreate(path, out CanonicalPath versions)
            ? Central(versions)
            : Beside;
    }

    /// <summary>Returns where versions go.</summary>
    /// <returns>Either the central file, or a note that each project carries its own.</returns>
    public override string ToString() =>
        IsCentral ? $"Central ({VersionsFilePath.FileName})" : "Beside the reference";
}

using System;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>What to do to a project's package references.</summary>
public enum PackageEditKind
{
    /// <summary>Add a reference the project does not have.</summary>
    Install = 0,

    /// <summary>Change the version of a reference the project already has.</summary>
    Update = 1,

    /// <summary>Take a reference away.</summary>
    Uninstall = 2,
}

/// <summary>
/// One package change, applied to one project.
/// </summary>
/// <remarks>
/// Deliberately one project rather than several. A change that spans projects is several of these,
/// and describing it that way keeps the caller in charge of what happens when the third one fails —
/// which is a question about their intent, not about editing XML.
/// </remarks>
public sealed record PackageEditRequest
{
    /// <summary>Gets what to do.</summary>
    public required PackageEditKind Kind { get; init; }

    /// <summary>Gets the project file to change.</summary>
    public required CanonicalPath ProjectFilePath
    {
        get;
        init
        {
            if (value.IsEmpty)
            {
                throw new ArgumentException("A package edit needs a project file.", nameof(ProjectFilePath));
            }

            field = value;
        }
    }

    /// <summary>Gets the package identifier, as NuGet spells it.</summary>
    /// <remarks>
    /// Matched case-insensitively against what the project already declares, because that is how
    /// NuGet compares identifiers — so asking for <c>serilog</c> finds an existing <c>Serilog</c>
    /// rather than adding a second reference to the same package.
    /// </remarks>
    public required string PackageId
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(PackageId));

            field = value;
        }
    }

    /// <summary>
    /// Gets the version to write, which <see cref="PackageEditKind.Install"/> and
    /// <see cref="PackageEditKind.Update"/> require and <see cref="PackageEditKind.Uninstall"/>
    /// ignores.
    /// </summary>
    /// <remarks>
    /// Written as given and never parsed. A version string is NuGet's to interpret — <c>[1.0,2.0)</c>
    /// and <c>1.0.0-*</c> are both meaningful and neither is a number — and a library that
    /// normalised it would silently change what the project asked for.
    /// </remarks>
    public string? Version { get; init; }

    /// <summary>Gets the <c>PrivateAssets</c> to write when installing, if any.</summary>
    public string? PrivateAssets { get; init; }

    /// <summary>Gets the <c>IncludeAssets</c> to write when installing, if any.</summary>
    public string? IncludeAssets { get; init; }

    /// <summary>Gets the <c>ExcludeAssets</c> to write when installing, if any.</summary>
    public string? ExcludeAssets { get; init; }

    /// <summary>Returns what this asks for.</summary>
    /// <returns>Something like <c>Install Serilog 4.1.0 into App.csproj</c>.</returns>
    public override string ToString() =>
        $"{Kind} {PackageId}{(Version is null ? string.Empty : " " + Version)} in {ProjectFilePath.FileName}";
}

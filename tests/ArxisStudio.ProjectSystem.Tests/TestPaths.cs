using System;
using System.IO;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// Builds absolute paths that are valid on whichever platform the tests are running on.
/// </summary>
/// <remarks>
/// A hard-coded <c>C:\src\App.csproj</c> is not a path on Linux and would make half these tests
/// unrunnable there, so the root comes from the platform and only the shape of the separators is
/// varied deliberately.
/// </remarks>
internal static class TestPaths
{
    /// <summary>Gets the platform's root, with a trailing separator.</summary>
    internal static string Root => OperatingSystem.IsWindows() ? "C:\\" : "/";

    /// <summary>Builds an absolute path using the platform's own separator.</summary>
    internal static string Native(params string[] segments) =>
        Root + string.Join(Path.DirectorySeparatorChar, segments);

    /// <summary>Builds the same path spelled with forward slashes.</summary>
    internal static string Slashes(params string[] segments) =>
        (OperatingSystem.IsWindows() ? "C:/" : "/") + string.Join('/', segments);

    /// <summary>Builds the same path spelled with backslashes.</summary>
    internal static string Backslashes(params string[] segments) =>
        (OperatingSystem.IsWindows() ? "C:\\" : "/") + string.Join('\\', segments);

    /// <summary>A canonical path for a project file, the shape most tests need.</summary>
    internal static CanonicalPath Project(string name = "App") =>
        CanonicalPath.Create(Native("src", name, name + ".csproj"));

    /// <summary>A canonical path for a solution file.</summary>
    internal static CanonicalPath Solution(string name = "App") =>
        CanonicalPath.Create(Native("src", name + ".sln"));

    /// <summary>A canonical path for anything else. Named <c>At</c> so it does not shadow
    /// <see cref="System.IO.Path"/> inside this class.</summary>
    internal static CanonicalPath At(params string[] segments) =>
        CanonicalPath.Create(Native(segments));
}

using System;
using System.IO;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// The one implementation of the repository's path policy: how a path is normalised, and how two
/// of them are compared.
/// </summary>
/// <remarks>
/// <para>
/// Project systems are path-heavy, and a path comparison scattered across a code base is a
/// comparison that is subtly different in each place it appears. Everything here is internal and
/// everything public goes through <see cref="CanonicalPath"/>, so there is exactly one answer to
/// "are these the same file" and exactly one place to change it.
/// </para>
/// <para>
/// No member of this type touches the file system. Normalisation is lexical.
/// </para>
/// </remarks>
internal static class CanonicalPathFormat
{
    /// <summary>
    /// How two canonical paths are compared, on every operating system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Case-insensitive, always, and this is the most contestable decision in the library — so,
    /// the argument. It is not "because Windows": Windows has had per-directory case sensitivity
    /// since 1803, and macOS is case-insensitive by default despite being Unix, so following the
    /// operating system would be wrong on both.
    /// </para>
    /// <para>
    /// It is because the domain is MSBuild, which compares paths case-insensitively everywhere.
    /// Item deduplication, project-reference matching and solution-to-project matching all do. A
    /// core that split what the engine populating it considers one project would be wrong about
    /// its own data.
    /// </para>
    /// <para>
    /// The failure modes are also not symmetric. The false split — a solution writing
    /// <c>src\App\App.csproj</c> where a project reference writes <c>..\App\app.csproj</c>,
    /// yielding two identities for one project — is routine in real repositories and corrupts the
    /// project graph. The false merge needs two files differing only in case in one directory on a
    /// case-sensitive volume, which already breaks MSBuild.
    /// </para>
    /// <para>
    /// Determinism decides the remainder: a comparer switched on the host operating system would
    /// make half these tests unrunnable on any given machine, and make a snapshot's meaning depend
    /// on where it was built.
    /// </para>
    /// </remarks>
    internal static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Matching <see cref="StringComparison"/> for direct string operations.</summary>
    internal static StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Normalises an absolute path, or explains why it is not one.
    /// </summary>
    /// <param name="path">The path to normalise.</param>
    /// <param name="normalised">The canonical form, when the input is acceptable.</param>
    /// <param name="error">Why the input was rejected, when it was.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> was normalised.</returns>
    internal static bool TryNormalize(string? path, out string normalised, out string? error)
    {
        normalised = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "A path cannot be null, empty or blank.";

            return false;
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            error = "A path cannot contain a null character.";

            return false;
        }

        string separated = Separate(path);

        // Rejects relative paths, rootless paths, and the drive-relative "C:file" form -- the one
        // shape Path.GetFullPath would otherwise resolve against a per-drive current directory.
        if (!Path.IsPathFullyQualified(separated))
        {
            error =
                $"'{path}' is not a fully qualified path. A canonical path is absolute; " +
                "combine a relative path with a base directory instead.";

            return false;
        }

        try
        {
            // The two-argument overload, always. Its base is this path's own root, which is itself
            // fully qualified, so the process's current directory is never consulted -- the
            // specification's "does not depend on the current working directory" made structural
            // rather than remembered.
            string root = Path.GetPathRoot(separated) ?? string.Empty;

            normalised = TrimTrailingSeparators(Path.GetFullPath(separated, root));
        }
        catch (ArgumentException exception)
        {
            error = $"'{path}' is not a valid path: {exception.Message}";

            return false;
        }
        catch (PathTooLongException exception)
        {
            error = $"'{path}' is too long: {exception.Message}";

            return false;
        }

        return true;
    }

    /// <summary>Normalises a path relative to an already-canonical directory.</summary>
    /// <param name="baseDirectory">The canonical directory to resolve against.</param>
    /// <param name="relativePath">The path to resolve. May itself be absolute.</param>
    /// <param name="normalised">The canonical form, when the input is acceptable.</param>
    /// <param name="error">Why the input was rejected, when it was.</param>
    /// <returns><see langword="true"/> when the combination was normalised.</returns>
    internal static bool TryNormalize(
        string baseDirectory,
        string? relativePath,
        out string normalised,
        out string? error)
    {
        normalised = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "A path cannot be null, empty or blank.";

            return false;
        }

        if (relativePath.Contains('\0', StringComparison.Ordinal))
        {
            error = "A path cannot contain a null character.";

            return false;
        }

        try
        {
            normalised = TrimTrailingSeparators(Path.GetFullPath(Separate(relativePath), baseDirectory));
        }
        catch (ArgumentException exception)
        {
            error = $"'{relativePath}' cannot be resolved against '{baseDirectory}': {exception.Message}";

            return false;
        }
        catch (PathTooLongException exception)
        {
            error = $"'{relativePath}' resolves to a path that is too long: {exception.Message}";

            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts both separator characters onto this platform's.
    /// </summary>
    /// <remarks>
    /// On Windows this is what the framework does anyway. On Unix it is a deliberate choice, and
    /// it has a cost: a Unix file name containing a literal backslash cannot be represented. The
    /// domain makes it the right trade — MSBuild writes <c>src\App\file.cs</c> into project files
    /// on every platform, and a library that read those literally on Linux would treat a whole
    /// project's items as one absurd file name. The cost is recorded in <c>docs/limitations.md</c>.
    /// </remarks>
    private static string Separate(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Drops trailing separators so that <c>C:\src\App\</c> and <c>C:\src\App</c> are one path,
    /// while leaving a root alone: <c>C:\</c> and <c>/</c> are separators all the way down.
    /// </summary>
    private static string TrimTrailingSeparators(string path)
    {
        int rootLength = (Path.GetPathRoot(path) ?? string.Empty).Length;
        int end = path.Length;

        while (end > rootLength && IsSeparator(path[end - 1]))
        {
            end--;
        }

        return end == path.Length ? path : path[..end];
    }

    private static bool IsSeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
}

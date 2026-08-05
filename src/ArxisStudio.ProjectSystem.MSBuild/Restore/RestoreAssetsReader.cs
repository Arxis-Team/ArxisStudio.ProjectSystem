using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Reads what restore resolved, from the <c>project.assets.json</c> it wrote.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-read with <c>System.Text.Json</c> rather than through <c>NuGet.ProjectModel</c>.
/// See <c>docs/adr/0012-restore-assets-are-read-not-resolved.md</c>: the whole family's boundary is
/// that no NuGet client library reaches any package here, and the part of this format that matters
/// is small, versioned and stable.
/// </para>
/// <para>
/// Unlike MSBuild evaluation, this needs no engine at all — it is a pure read of a documented file.
/// So it is tested with hand-written fixtures rather than by restoring anything, which keeps the
/// package cache and the network out of the test suite exactly as the contract requires.
/// </para>
/// <para>
/// Two shapes in this file mean "there is no assembly here", and both would become a nonsense path
/// if taken literally. <c>_._</c> is restore's placeholder for "this package contributes nothing of
/// that kind" — every <c>ExcludeAssets="runtime"</c> produces one. And an entry of type
/// <c>project</c> carries <c>bin/placeholder/Name.dll</c>, which never exists: a referenced
/// project's real output comes from evaluating that project, which the provider already does.
/// </para>
/// </remarks>
internal static class RestoreAssetsReader
{
    /// <summary>The file name restore writes, when a project does not say otherwise.</summary>
    internal const string FileName = "project.assets.json";

    /// <summary>Reads the packages resolved for one framework.</summary>
    /// <param name="assetsFile">The assets file restore wrote.</param>
    /// <param name="targetFramework">The framework whose resolution is wanted.</param>
    /// <param name="packages">What restore resolved, empty when it resolved nothing for that framework.</param>
    /// <param name="error">Why the file could not be read, when it could not.</param>
    /// <returns><see langword="true"/> when the file was read.</returns>
    internal static bool TryRead(
        CanonicalPath assetsFile,
        string? targetFramework,
        out ImmutableArray<ResolvedPackage> packages,
        out string? error)
    {
        packages = [];
        error = null;

        JsonDocument document;

        try
        {
            using FileStream stream = File.OpenRead(assetsFile.Value);

            document = JsonDocument.Parse(stream);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";

            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The assets file does not contain a JSON object.";

                return false;
            }

            if (!TryFindTarget(root, targetFramework, out JsonElement target))
            {
                // Restore produced nothing for this framework. Not a failure to read the file: a
                // project can be restored for one framework and asked about another.
                return true;
            }

            packages = Read(root, target);

            return true;
        }
    }

    /// <summary>
    /// The assets file's location, which the evaluation itself reports.
    /// </summary>
    /// <remarks>
    /// Read from <c>ProjectAssetsFile</c> rather than assumed to be <c>obj/project.assets.json</c>,
    /// because a project is free to move its intermediate output and plenty do.
    /// </remarks>
    internal static CanonicalPath Locate(EvaluatedProject project)
    {
        string? declared = project.Properties.GetValueOrDefault("ProjectAssetsFile");

        if (string.IsNullOrWhiteSpace(declared) || project.FullPath.Directory.IsEmpty)
        {
            return CanonicalPath.None;
        }

        try
        {
            return CanonicalPath.Create(project.FullPath.Directory, declared);
        }
        catch (ArgumentException)
        {
            return CanonicalPath.None;
        }
    }

    /// <summary>
    /// Finds the entry for a framework, allowing for a runtime identifier appended to the key.
    /// </summary>
    /// <remarks>
    /// A restore for a specific runtime writes <c>net10.0/win-x64</c> rather than <c>net10.0</c>, so
    /// an exact match alone would silently find nothing on a project that names a runtime.
    /// </remarks>
    private static bool TryFindTarget(JsonElement root, string? targetFramework, out JsonElement target)
    {
        target = default;

        if (!root.TryGetProperty("targets", out JsonElement targets)
            || targets.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            if (targets.TryGetProperty(targetFramework, out target))
            {
                return true;
            }

            foreach (JsonProperty candidate in targets.EnumerateObject())
            {
                if (candidate.Name.StartsWith(targetFramework + "/", StringComparison.OrdinalIgnoreCase))
                {
                    target = candidate.Value;

                    return true;
                }
            }

            return false;
        }

        // No framework asked for: a single-target project has exactly one, and using it is better
        // than reporting nothing. More than one is ambiguous, and guessing would be worse.
        JsonElement.ObjectEnumerator all = targets.EnumerateObject();
        bool any = false;

        foreach (JsonProperty candidate in all)
        {
            if (any)
            {
                return false;
            }

            target = candidate.Value;
            any = true;
        }

        return any;
    }

    private static ImmutableArray<ResolvedPackage> Read(JsonElement root, JsonElement target)
    {
        CanonicalPath packageRoot = PackageRoot(root);
        Dictionary<string, string> libraryPaths = LibraryPaths(root);
        HashSet<string> direct = DirectDependencies(root);

        ImmutableArray<ResolvedPackage>.Builder packages = ImmutableArray.CreateBuilder<ResolvedPackage>();

        foreach (JsonProperty entry in target.EnumerateObject())
        {
            // Only packages. A "project" entry is a project reference, which is already modelled as
            // one and whose real output comes from evaluating it rather than from here.
            if (!string.Equals(Text(entry.Value, "type"), "package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (string id, string version) = Split(entry.Name);

            if (id.Length == 0)
            {
                continue;
            }

            CanonicalPath libraryRoot = LibraryRoot(packageRoot, libraryPaths, entry.Name);

            packages.Add(new ResolvedPackage
            {
                PackageId = id,
                Version = version,
                CompileAssemblies = Assemblies(entry.Value, "compile", libraryRoot),
                RuntimeAssemblies = Assemblies(entry.Value, "runtime", libraryRoot),
                Dependencies = Names(entry.Value, "dependencies"),
                IsDirect = direct.Contains(id),
            });
        }

        return packages.ToImmutable();
    }

    private static ImmutableArray<CanonicalPath> Assemblies(
        JsonElement entry,
        string section,
        CanonicalPath libraryRoot)
    {
        if (libraryRoot.IsEmpty
            || !entry.TryGetProperty(section, out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        ImmutableArray<CanonicalPath>.Builder paths = ImmutableArray.CreateBuilder<CanonicalPath>();

        foreach (JsonProperty asset in assets.EnumerateObject())
        {
            if (IsPlaceholder(asset.Name))
            {
                continue;
            }

            try
            {
                paths.Add(CanonicalPath.Create(libraryRoot, asset.Name));
            }
            catch (ArgumentException)
            {
                // An asset key that is not a usable relative path. Nothing to report: the rest of
                // the package is still perfectly readable.
            }
        }

        return paths.ToImmutable();
    }

    /// <summary>
    /// Whether an asset key is restore's "nothing here" marker rather than a file.
    /// </summary>
    /// <remarks>
    /// <c>_._</c> is an empty file restore places so that a package can say it contributes nothing
    /// for a framework. <c>bin/placeholder/</c> belongs to project entries, which are skipped
    /// earlier, and is matched here as well so that a future caller cannot reintroduce the bug by
    /// widening the entry filter.
    /// </remarks>
    private static bool IsPlaceholder(string assetKey) =>
        string.Equals(Path.GetFileName(assetKey), "_._", StringComparison.Ordinal)
        || assetKey.StartsWith("bin/placeholder/", StringComparison.OrdinalIgnoreCase);

    private static CanonicalPath PackageRoot(JsonElement root)
    {
        if (root.TryGetProperty("packageFolders", out JsonElement folders)
            && folders.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty folder in folders.EnumerateObject())
            {
                if (CanonicalPath.TryCreate(folder.Name, out CanonicalPath path))
                {
                    return path;
                }
            }
        }

        return CanonicalPath.None;
    }

    private static Dictionary<string, string> LibraryPaths(JsonElement root)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("libraries", out JsonElement libraries)
            && libraries.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty library in libraries.EnumerateObject())
            {
                if (Text(library.Value, "path") is { Length: > 0 } path)
                {
                    paths[library.Name] = path;
                }
            }
        }

        return paths;
    }

    private static CanonicalPath LibraryRoot(
        CanonicalPath packageRoot,
        Dictionary<string, string> libraryPaths,
        string key)
    {
        if (packageRoot.IsEmpty || !libraryPaths.TryGetValue(key, out string? relative))
        {
            return CanonicalPath.None;
        }

        try
        {
            return CanonicalPath.Create(packageRoot, relative);
        }
        catch (ArgumentException)
        {
            return CanonicalPath.None;
        }
    }

    /// <summary>
    /// The packages the project asked for itself, as opposed to those that arrived transitively.
    /// </summary>
    /// <remarks>
    /// Entries read like <c>Serilog &gt;= 4.1.0</c>, so the id is everything before the first space.
    /// A project reference appears here too and simply never matches a package id.
    /// </remarks>
    private static HashSet<string> DirectDependencies(JsonElement root)
    {
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("projectFileDependencyGroups", out JsonElement groups)
            || groups.ValueKind != JsonValueKind.Object)
        {
            return direct;
        }

        foreach (JsonProperty group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement dependency in group.Value.EnumerateArray())
            {
                if (dependency.GetString() is { Length: > 0 } text)
                {
                    int space = text.IndexOf(' ', StringComparison.Ordinal);

                    direct.Add(space < 0 ? text : text[..space]);
                }
            }
        }

        return direct;
    }

    private static ImmutableArray<string> Names(JsonElement entry, string section)
    {
        if (!entry.TryGetProperty(section, out JsonElement values)
            || values.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();

        foreach (JsonProperty value in values.EnumerateObject())
        {
            names.Add(value.Name);
        }

        return names.ToImmutable();
    }

    private static (string Id, string Version) Split(string key)
    {
        int slash = key.IndexOf('/', StringComparison.Ordinal);

        return slash < 0 ? (key, string.Empty) : (key[..slash], key[(slash + 1)..]);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

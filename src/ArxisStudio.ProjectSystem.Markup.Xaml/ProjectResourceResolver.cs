using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml.Loader;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Resolves <c>avares://</c> URIs to the files in the project, rather than to what was last built.
/// </summary>
/// <remarks>
/// <para>
/// Markup already resolves <c>avares</c> out of loaded assemblies, and at run time that is the right
/// answer. At design time it is the wrong one: the resource embedded in the last build is exactly
/// the version the user is editing away from, so a style sheet they changed a minute ago would keep
/// resolving to what it said before. This resolver answers from the project instead, which is what
/// makes an edit visible without a build.
/// </para>
/// <para>
/// Which file a URI names is <see cref="ProjectResourceMap"/>'s to say, and it says the same thing
/// to <see cref="ProjectMarkupSourceProvider"/>. Resolving a resource and opening a document are the
/// same question asked twice.
/// </para>
/// </remarks>
public sealed class ProjectResourceResolver : IXamlResourceResolver
{
    private readonly ProjectResourceMap _map;

    /// <summary>Creates a resolver over a map.</summary>
    /// <param name="map">Which file each URI names.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    public ProjectResourceResolver(ProjectResourceMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        _map = map;
    }

    /// <summary>Gets how many resources this resolver can answer for.</summary>
    public int Count => _map.Count;

    /// <summary>Builds a resolver over every project in a snapshot.</summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <returns>The resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static ProjectResourceResolver Create(SolutionSnapshot snapshot) =>
        new(ProjectResourceMap.Create(snapshot));

    /// <inheritdoc />
    public ValueTask<XamlResource?> ResolveAsync(
        Uri resourceUri, Uri? baseUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_map.TryGetFile(resourceUri, baseUri, out CanonicalPath file) || !File.Exists(file.Value))
        {
            // Not knowing a URI is an ordinary answer. The composite behind this one goes on to ask
            // the assemblies, which is the right place for a resource of a package's own.
            return new ValueTask<XamlResource?>((XamlResource?)null);
        }

        string path = file.Value;

        return new ValueTask<XamlResource?>(new XamlResource(
            ProjectResourceMap.Combine(resourceUri, baseUri),
            _ => new ValueTask<Stream>(ProjectFiles.OpenRead(path))));
    }
}

/// <summary>Opening a project file the way a designer has to.</summary>
internal static class ProjectFiles
{
    /// <summary>
    /// Opens a file for reading without standing in anybody's way.
    /// </summary>
    /// <remarks>
    /// Shared for writing and deletion, because the file being read is one an editor and a build are
    /// both entitled to replace while a designer is looking at it. A designer that locked the
    /// documents it displayed would break the thing it exists to support.
    /// </remarks>
    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
}

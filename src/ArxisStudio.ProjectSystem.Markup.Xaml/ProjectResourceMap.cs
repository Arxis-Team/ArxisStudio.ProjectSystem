using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// What file an <c>avares://</c> URI names in the project, before anything is built.
/// </summary>
/// <remarks>
/// <para>
/// One definition of that mapping, shared by everything in this package that needs it. Resolving a
/// resource and opening a document are different questions asked of the same fact, and answering
/// them from two maps would let them disagree about which file a URI means — which would show up as
/// a designer previewing one document and reporting errors against another.
/// </para>
/// <para>
/// An <c>avares</c> URI is <c>avares://{assembly}/{path}</c>, where the assembly is the producing
/// project's assembly name and the path is the resource's path within it — which the Avalonia SDK
/// takes from the item's path relative to the project directory, or from its <c>Link</c> when it has
/// one. That mapping is reversed here, once.
/// </para>
/// <para>
/// Only <c>AvaloniaResource</c> items are mapped, because those are the ones that become
/// <c>avares</c> resources. Exposing every file in the project under an <c>avares</c> URI would
/// answer questions the runtime would answer differently, and a designer that disagrees with the
/// running application about what a URI means is worse than one that cannot resolve it.
/// </para>
/// </remarks>
public sealed class ProjectResourceMap
{
    /// <summary>The item type the Avalonia SDK turns into an embedded resource.</summary>
    private const string AvaloniaResource = "AvaloniaResource";

    internal const string Scheme = "avares";

    private readonly ImmutableDictionary<string, CanonicalPath> _resources;

    private ProjectResourceMap(ImmutableDictionary<string, CanonicalPath> resources) =>
        _resources = resources;

    /// <summary>Gets how many resources this map knows.</summary>
    public int Count => _resources.Count;

    /// <summary>
    /// Builds a map over every project in a snapshot.
    /// </summary>
    /// <remarks>
    /// The whole snapshot rather than one project, because a document routinely names a resource
    /// belonging to a project it references — a shared theme in a control library is the ordinary
    /// case, not an exotic one.
    /// </remarks>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <returns>The map.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static ProjectResourceMap Create(SolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Ordinal, and deliberately: an assembly name is case-sensitive, and folding case here would
        // merge two assemblies that differ only by it.
        ImmutableDictionary<string, CanonicalPath>.Builder resources =
            ImmutableDictionary.CreateBuilder<string, CanonicalPath>(StringComparer.Ordinal);

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            string assembly = AssemblyNameOf(project);

            foreach (ProjectItem item in project.Items)
            {
                if (!string.Equals(item.ItemType, AvaloniaResource, StringComparison.OrdinalIgnoreCase)
                    || item.FullPath.IsEmpty)
                {
                    continue;
                }

                if (ResourcePath(project, item) is { Length: > 0 } path)
                {
                    // First declaration wins, so a project listing a file twice under different
                    // metadata does not depend on which copy came last.
                    resources.TryAdd($"{Scheme}://{assembly}/{path}", item.FullPath);
                }
            }
        }

        return new ProjectResourceMap(resources.ToImmutable());
    }

    /// <summary>
    /// Finds the file an <c>avares</c> URI names.
    /// </summary>
    /// <param name="resourceUri">The URI, absolute or relative to <paramref name="baseUri"/>.</param>
    /// <param name="baseUri">What a relative URI is resolved against.</param>
    /// <param name="file">The file, when this map knows the URI.</param>
    /// <returns>
    /// <see langword="true"/> when the URI names a resource of a project in the snapshot. Says
    /// nothing about whether the file is still there — a caller that is going to open it finds that
    /// out by opening it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="resourceUri"/> is <see langword="null"/>.</exception>
    public bool TryGetFile(Uri resourceUri, Uri? baseUri, [NotNullWhen(true)] out CanonicalPath file)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);

        file = CanonicalPath.None;

        bool combinedWithBase = !resourceUri.IsAbsoluteUri;

        Uri target = resourceUri.IsAbsoluteUri || baseUri is null
            || !Uri.TryCreate(baseUri, resourceUri, out Uri? combined)
                ? resourceUri
                : combined;

        if (!target.IsAbsoluteUri || !string.Equals(target.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The authority comes from whichever URI was written by hand. Combining a relative
        // reference against a base lowercases the host irrecoverably -- measured, not assumed --
        // and the host of an avares URI is an assembly name, so taking it from the combined result
        // would look for MyApp under myapp and find nothing.
        string key = Key(combinedWithBase && baseUri is not null ? baseUri : resourceUri, target);

        return _resources.TryGetValue(key, out file);
    }

    /// <summary>Resolves a URI against a base the way this map does, for a caller that needs the result.</summary>
    /// <param name="resourceUri">The URI, absolute or relative.</param>
    /// <param name="baseUri">What a relative URI is resolved against.</param>
    /// <returns>The absolute URI, or the original when it cannot be combined.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resourceUri"/> is <see langword="null"/>.</exception>
    public static Uri Combine(Uri resourceUri, Uri? baseUri)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);

        return resourceUri.IsAbsoluteUri || baseUri is null
            || !Uri.TryCreate(baseUri, resourceUri, out Uri? combined)
                ? resourceUri
                : combined;
    }

    /// <summary>
    /// The key a URI is looked up under: the assembly name as somebody wrote it, and the path as
    /// URI arithmetic worked it out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two halves from two places, and each for a reason. The authority comes from the original
    /// text of whichever URI was written by hand, because <see cref="Uri"/> lowercases a host — it
    /// assumes DNS, and here the host is a case-sensitive assembly name.
    /// </para>
    /// <para>
    /// The path comes from <see cref="Uri.AbsolutePath"/> of the resolved URI, because that is real
    /// URI arithmetic: it is what turns <c>../Themes/Dark.axaml</c> into a path, and hand-rolling
    /// that would be reimplementing RFC 3986 badly. It is unescaped afterwards, because the map is
    /// keyed by file names and a file called <c>My View.axaml</c> is not called
    /// <c>My%20View.axaml</c>.
    /// </para>
    /// </remarks>
    private static string Key(Uri written, Uri resolved) =>
        $"{Scheme}://{Authority(written)}{Uri.UnescapeDataString(resolved.AbsolutePath)}";

    /// <summary>The authority as written, before <see cref="Uri"/> folded its case.</summary>
    private static string Authority(Uri uri)
    {
        string original = uri.OriginalString;
        int start = original.IndexOf("//", StringComparison.Ordinal);

        if (start < 0)
        {
            return uri.IsAbsoluteUri ? uri.Host : string.Empty;
        }

        int end = original.IndexOf('/', start + 2);

        return end < 0 ? original[(start + 2)..] : original[(start + 2)..end];
    }

    /// <summary>The name of the assembly a project produces, which is what an avares host is.</summary>
    private static string AssemblyNameOf(ProjectSnapshot project) =>
        project.Properties.TryGetValue("AssemblyName", out string? assembly) && assembly.Length > 0
            ? assembly
            : project.Name;

    /// <summary>
    /// An item's path within the assembly: its <c>Link</c> when it has one, and otherwise its path
    /// relative to the project directory.
    /// </summary>
    private static string? ResourcePath(ProjectSnapshot project, ProjectItem item)
    {
        if (item.Link is { Length: > 0 } link)
        {
            return Normalise(link);
        }

        if (!item.FullPath.StartsWith(project.ProjectDirectory))
        {
            // Outside the project and not linked. Avalonia would give it a path derived from
            // something this model cannot see, and guessing would produce a URI that resolves here
            // and nowhere else.
            return null;
        }

        string relative = item.FullPath.Value[project.ProjectDirectory.Value.Length..];

        return Normalise(relative);
    }

    private static string Normalise(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}

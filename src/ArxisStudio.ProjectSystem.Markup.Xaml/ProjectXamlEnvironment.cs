using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml.Loader;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Answers Markup's assembly question out of a project's own build.
/// </summary>
/// <remarks>
/// The adapter in one sentence: Markup asks "what is the assembly called <c>MyControls</c>", the
/// project model knows which file that is, and this joins the two. Markup never learns that a
/// project exists, and the project model never learns that anybody is loading anything.
/// </remarks>
public sealed class ProjectAssemblyResolver : IXamlAssemblyResolver
{
    private readonly ProjectAssemblyContext _context;

    /// <summary>Creates a resolver over a project's assemblies.</summary>
    /// <param name="context">The context that owns them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public ProjectAssemblyResolver(ProjectAssemblyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Synchronous underneath, and honestly so: resolving is a dictionary lookup and, at most, a
    /// file read. The interface is asynchronous because a future resolver might reach a build or a
    /// worker process, not because this one does.
    /// </remarks>
    public ValueTask<Assembly?> ResolveAsync(AssemblyName assemblyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        cancellationToken.ThrowIfCancellationRequested();

        return new ValueTask<Assembly?>(_context.Resolve(assemblyName));
    }
}

/// <summary>
/// Builds the environment Markup loads a document in, from a project snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The composition root of the adapter, and deliberately the only place that knows both families'
/// vocabularies at once.
/// </para>
/// <para>
/// The resolver order matters and is the snapshot's: the project's own assemblies first, then
/// whatever the host process already has. A document naming a control the user has just rebuilt
/// gets the rebuilt one, not a copy that happened to be loaded earlier.
/// </para>
/// </remarks>
public static class ProjectXamlEnvironment
{
    /// <summary>
    /// Builds an environment for loading documents belonging to one project.
    /// </summary>
    /// <remarks>
    /// The caller keeps the <paramref name="context"/> and disposes it when the environment is
    /// finished with — which is the whole reload cycle: build, new context, new environment, swap,
    /// drop the old pair. Disposing it here would be wrong, because the environment outlives this
    /// call and nothing here knows when it stops being looked at.
    /// </remarks>
    /// <param name="context">The project's assemblies.</param>
    /// <param name="resources">
    /// Which file each <c>avares</c> URI names, or <see langword="null"/> to resolve them only out
    /// of built assemblies.
    /// </param>
    /// <param name="sourceProvider">
    /// Where documents are read from beyond the project's own, or <see langword="null"/> for files
    /// on disk.
    /// </param>
    /// <returns>The environment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static XamlLoadEnvironment Create(
        ProjectAssemblyContext context,
        ProjectResourceMap? resources = null,
        IMarkupSourceProvider? sourceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The project first, then whatever the process already has. LoadedAssemblyResolver is what
        // supplies Avalonia and anything else the host references, and it comes second so a
        // rebuilt control library is never shadowed by an older copy of itself.
        var assemblyResolver = new CompositeAssemblyResolver(
            new ProjectAssemblyResolver(context),
            LoadedAssemblyResolver.Instance);

        // Same principle for resources, and it matters more: the copy embedded in the last build is
        // precisely the version the user is editing away from, so the project's own file has to be
        // asked first or an edit stays invisible until a rebuild.
        var resourceResolvers = new List<IXamlResourceResolver>(3);

        if (resources is not null)
        {
            resourceResolvers.Add(new ProjectResourceResolver(resources));
        }

        resourceResolvers.Add(new AvaloniaResourceResolver(assemblyResolver));
        resourceResolvers.Add(FileResourceResolver.Instance);

        // Documents arrive both ways in one session: opened by path, and followed to by the avares
        // URI a StyleInclude names them with. Markup's file provider answers only the first, which
        // is correct for it and is the gap this adapter exists to close.
        IMarkupSourceProvider sources = sourceProvider ?? new FileMarkupSourceProvider();

        if (resources is not null)
        {
            sources = new CompositeMarkupSourceProvider(
                new ProjectMarkupSourceProvider(resources), sources);
        }

        return new XamlLoadEnvironment
        {
            SourceProvider = sources,
            AssemblyResolver = assemblyResolver,
            TypeResolver = new XamlTypeResolver(assemblyResolver, Searchable(context)),
            ResourceResolver = new CompositeResourceResolver(resourceResolvers),
        };
    }

    /// <summary>
    /// The project's own assemblies, as assemblies, for the type resolver to search.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the project is invisible to every name written the way people write them. A
    /// resolver answers a name; it does not enumerate — so <c>clr-namespace:Ns;assembly=App</c>
    /// worked and a bare <c>using:Ns</c> did not, and <c>x:Class</c> is resolved as a bare
    /// <c>using:</c>. Every form with a code-behind class failed to load, and the assembly holding
    /// the class was sitting in the context at the time.
    /// </para>
    /// <para>
    /// Only what the projects build, not the packages. Those are the few the collectible context
    /// owns and the ones a rebuild changes, and they are where an <c>x:Class</c> and a project's own
    /// controls live. Loading every restored package eagerly would be a great deal of work done
    /// before anything asked for it, and — because package assemblies go to the default context and
    /// stay — an irreversible one. A type in a package is still reached by naming its assembly, and
    /// a package the host has already loaded is found anyway.
    /// </para>
    /// <para>
    /// An assembly that will not load is skipped rather than reported. It is not this method's
    /// question: the document that names a type from it produces the diagnostic that matters, and it
    /// says which type could not be found rather than which file could not be read.
    /// </para>
    /// </remarks>
    private static List<Assembly> Searchable(ProjectAssemblyContext context)
    {
        var searchable = new List<Assembly>();

        foreach (RuntimeAssemblyReference reference in context.Assemblies)
        {
            if (reference.Origin is not (RuntimeAssemblyOrigin.Project or RuntimeAssemblyOrigin.ProjectReference))
            {
                continue;
            }

            string simpleName = System.IO.Path.GetFileNameWithoutExtension(reference.Path.FileName);

            if (simpleName.Length > 0 && context.Resolve(new AssemblyName(simpleName)) is { } assembly)
            {
                searchable.Add(assembly);
            }
        }

        return searchable;
    }

    /// <summary>
    /// Builds a context and an environment for one project in one step.
    /// </summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <param name="project">The project whose documents will be loaded.</param>
    /// <param name="sourceProvider">
    /// Where documents are read from, or <see langword="null"/> for files on disk.
    /// </param>
    /// <returns>
    /// The environment and the context behind it. The caller owns the context and disposes it when
    /// the environment is done with.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static (XamlLoadEnvironment Environment, ProjectAssemblyContext Context) CreateFor(
        SolutionSnapshot snapshot,
        ProjectIdentity project,
        IMarkupSourceProvider? sourceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ProjectAssemblyContext context = ProjectAssemblyContext.Create(snapshot, project);

        // One map, shared by the resource resolver and the source provider, so the two cannot
        // disagree about which file a URI names.
        return (Create(context, ProjectResourceMap.Create(snapshot), sourceProvider), context);
    }
}

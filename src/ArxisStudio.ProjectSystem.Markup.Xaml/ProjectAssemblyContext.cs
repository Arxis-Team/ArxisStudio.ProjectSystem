using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Threading;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Loads a project's assemblies so that a rebuilt one can be loaded again.
/// </summary>
/// <remarks>
/// <para>
/// A designer rebuilds the user's control library all day, and .NET will not replace an assembly
/// that has been loaded into the default context. So the project's own outputs go into a
/// collectible <see cref="AssemblyLoadContext"/> of their own: build a new context for the new
/// build, hand it to a new load environment, and let the old one go.
/// </para>
/// <para>
/// <b>Only what gets rebuilt goes in the collectible context.</b> Packages and anything the host
/// already has — Avalonia above all — are resolved from the default context instead. This is not an
/// optimisation. Two copies of Avalonia loaded into two contexts produce two
/// <c>Avalonia.Controls.Button</c> types that are not assignable to one another, and the failure
/// arrives much later than the mistake, as a cast that cannot possibly fail and does.
/// </para>
/// <para>
/// <b>Assemblies are read into memory rather than loaded from their path,</b> which is the detail
/// this type exists for. <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/> holds the file open,
/// and the next build then fails to write its own output — so the very rebuild this class exists to
/// support would be the thing it prevented.
/// </para>
/// <para>
/// <b>One generation for a design set, not one per project.</b> A designer shows forms of several
/// projects at once — an application and the control library it references — and a library two
/// projects share is one assembly in the process, loaded once
/// (<see cref="Create(SolutionSnapshot, IEnumerable{ProjectIdentity}, string?)"/>). What each
/// project's documents may see is that project's own closure (<see cref="AssembliesOf"/>), and what
/// the set could not load as the projects asked is said in <see cref="Diagnostics"/>.
/// </para>
/// <para>
/// <b>A closure includes the packages that map a XAML namespace.</b> A document names such a package's
/// types by the namespace, never by the assembly — <c>FluentTheme</c> is written in
/// <c>https://github.com/avaloniaui</c> and lives in <c>Avalonia.Themes.Fluent</c> — so a closure of
/// what the projects build left them to whatever the host happened to have loaded, and a host with a
/// theme of its own lost every form's (ADR 0027, amended 2026-10-04). Whether a package maps one is read
/// from its metadata; a package that maps nothing is not loaded for being asked.
/// </para>
/// <para>
/// <b>Everything is loaded at once, when the generation is created.</b> Loading an assembly when a
/// document first names it let a build that ran in between put two builds into one generation —
/// the library from before it and the application from after. Created when no build is running, a
/// generation is one build of everything.
/// </para>
/// <para>
/// Nothing here decides <em>when</em> to unload. That is the host's, because only the host knows
/// whether anything is still looking at the old objects.
/// </para>
/// </remarks>
public sealed partial class ProjectAssemblyContext
    : ArxisStudio.Markup.Xaml.Loader.IXamlCompilationScope, IDisposable
{
    /// <summary>
    /// The load context, until it is unloaded and then nothing.
    /// </summary>
    /// <remarks>
    /// Dropped on unload rather than kept, because a host that still holds this object would
    /// otherwise still hold the generation through it — and holding the husk is exactly what a
    /// host does between asking a generation to go and proving that it went. See
    /// <see cref="TryReclaimAsync"/>, which cannot answer honestly while this field is set.
    /// </remarks>
    private AssemblyLoadContext? _context;

    private readonly Dictionary<string, CanonicalPath> _rebuildable;
    private readonly Dictionary<string, CanonicalPath> _stable;
    private readonly Dictionary<string, Assembly?> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStamp> _stamps;
    private readonly Dictionary<ProjectIdentity, ImmutableArray<string>> _closures;
    private readonly Dictionary<ProjectIdentity, string> _outputs;
    private readonly HashSet<string> _xamlPackages;
    private readonly Dictionary<string, CanonicalPath> _packagesLoaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private int _disposed;

    private ProjectAssemblyContext(
        string name,
        ImmutableArray<ProjectIdentity> projects,
        WorkspaceVersion version,
        ImmutableArray<RuntimeAssemblyReference> assemblies,
        DesignSet set)
    {
        Name = name;
        Projects = projects;
        Version = version;
        Assemblies = assemblies;
        Diagnostics = set.Diagnostics;

        _rebuildable = set.Rebuildable;
        _stable = set.Stable;
        _closures = set.Closures;
        _outputs = set.Outputs;
        _xamlPackages = set.XamlPackages;
        _stamps = Stamp(set.Rebuildable);

        var context = new AssemblyLoadContext(name, isCollectible: true);

        // The hook fires for a dependency the loader could not satisfy itself, which is how a
        // referenced project's output is found without anybody naming it up front.
        context.Resolving += (_, name) => Resolve(name);

        _context = context;
    }

    /// <summary>Gets the name this context was created with, which shows up in diagnostics.</summary>
    public string Name { get; }

    /// <summary>Gets the project these assemblies belong to — for a design set, the first of <see cref="Projects"/>.</summary>
    public ProjectIdentity Project => Projects[0];

    /// <summary>Gets the projects whose documents this generation's types serve, in the order given.</summary>
    public ImmutableArray<ProjectIdentity> Projects { get; }

    /// <summary>
    /// Gets what the generation could not load as the projects asked: two assemblies of one name
    /// (<see cref="ProjectDesignDiagnosticCodes.AssemblyNameConflict"/>), and a project's build of an
    /// assembly the host process already has (<see cref="ProjectDesignDiagnosticCodes.ShadowedByHost"/>).
    /// </summary>
    public ImmutableArray<ProjectDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets the workspace version this context was built from.
    /// </summary>
    /// <remarks>
    /// What makes a stale result rejectable. A designer builds, loads, and shows — and by the time
    /// the showing happens the model may have moved on twice. Comparing this against the current
    /// snapshot's version says whether what is about to be displayed describes the project as it is
    /// now, and the comparison is one integer rather than a walk over everything that might have
    /// changed. See <see cref="IsCurrentFor"/>.
    /// </remarks>
    public WorkspaceVersion Version { get; }

    /// <summary>Gets what the projects need, each file once, in the order a resolver should consult it.</summary>
    public ImmutableArray<RuntimeAssemblyReference> Assemblies { get; }

    /// <summary>Gets a value indicating whether this context has been unloaded.</summary>
    public bool IsUnloaded => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Creates a context for one project of a snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <param name="project">The project whose assemblies are wanted.</param>
    /// <param name="name">
    /// A name for the context, or <see langword="null"/> to derive one from the project and the
    /// snapshot's version — which is what makes two generations of the same project tell apart in a
    /// debugger.
    /// </param>
    /// <returns>The context, every assembly of it loaded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static ProjectAssemblyContext Create(
        SolutionSnapshot snapshot, ProjectIdentity project, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Create(snapshot, [project], name ?? Describe(snapshot, project));
    }

    /// <summary>
    /// Creates one generation for a design set: every project whose documents a designer shows, and
    /// what they reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The union of what each project needs at run time, each assembly once: a library two projects
    /// reference is one assembly, and a form of either sees the same <c>Type</c>. Two different files
    /// of one name cannot both be loaded; the first in the set's order is, and
    /// <see cref="Diagnostics"/> says so. A project's own build of an assembly the host process
    /// already has is not loaded either — the process's copy answers for the name — and that is said
    /// too.
    /// </para>
    /// <para>
    /// Every assembly the projects build is loaded before this returns, so the generation is one
    /// build of everything; create it when no build is running.
    /// </para>
    /// </remarks>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <param name="projects">The projects, in the order their assemblies should win.</param>
    /// <param name="name">
    /// A name for the context, or <see langword="null"/> to derive one from the projects and the
    /// snapshot's version.
    /// </param>
    /// <returns>The context, every assembly of it loaded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="projects"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="projects"/> names no project.</exception>
    public static ProjectAssemblyContext Create(
        SolutionSnapshot snapshot, IEnumerable<ProjectIdentity> projects, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(projects);

        ImmutableArray<ProjectIdentity> set = [.. projects.Where(static project => !project.IsEmpty).Distinct()];

        if (set.IsEmpty)
        {
            throw new ArgumentException("A generation is of at least one project.", nameof(projects));
        }

        (ImmutableArray<RuntimeAssemblyReference> assemblies, DesignSet design) = Gather(snapshot, set);

        var context = new ProjectAssemblyContext(
            name ?? Describe(snapshot, set), set, snapshot.Version, assemblies, design);

        context.LoadEverything();

        return context;
    }

    /// <summary>What a generation is made of, worked out from the snapshot before anything loads.</summary>
    private sealed record DesignSet(
        Dictionary<string, CanonicalPath> Rebuildable,
        Dictionary<string, CanonicalPath> Stable,
        Dictionary<ProjectIdentity, ImmutableArray<string>> Closures,
        Dictionary<ProjectIdentity, string> Outputs,
        HashSet<string> XamlPackages,
        ImmutableArray<ProjectDiagnostic> Diagnostics);

    private static (ImmutableArray<RuntimeAssemblyReference> Assemblies, DesignSet Set) Gather(
        SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> set)
    {
        var rebuildable = new Dictionary<string, CanonicalPath>(StringComparer.OrdinalIgnoreCase);
        var stable = new Dictionary<string, CanonicalPath>(StringComparer.OrdinalIgnoreCase);
        var closures = new Dictionary<ProjectIdentity, ImmutableArray<string>>();
        var outputs = new Dictionary<ProjectIdentity, string>();
        var xamlPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = ImmutableArray.CreateBuilder<ProjectDiagnostic>();
        var conflicting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<CanonicalPath>();
        ImmutableArray<RuntimeAssemblyReference>.Builder assemblies = ImmutableArray.CreateBuilder<RuntimeAssemblyReference>();

        // The set first, in its order, which is the order assemblies win in; then every project they
        // reference, whose assemblies are already in, so that each has a closure of its own.
        var projects = new List<ProjectIdentity>(set);

        for (int next = 0; next < projects.Count; next++)
        {
            ProjectIdentity project = projects[next];
            var closure = new List<string>();

            foreach (RuntimeAssemblyReference assembly in snapshot.GetRuntimeAssemblies(project))
            {
                // Keyed by file name, which is the assembly's simple name for anything a build
                // produces: an output path is its directory plus its assembly name plus an extension,
                // and a package lays its files out the same way. A file whose contents disagree with
                // its name resolves under the name on disk, which is the only name anybody could ask
                // for without opening it first.
                string simpleName = Path.GetFileNameWithoutExtension(assembly.Path.Value);

                if (simpleName.Length == 0)
                {
                    continue;
                }

                if (paths.Add(assembly.Path))
                {
                    assemblies.Add(assembly);
                }

                // First one wins, which is why the snapshot returns them in priority order and the
                // set is walked in the order it was given.
                Dictionary<string, CanonicalPath> destination =
                    assembly.Origin == RuntimeAssemblyOrigin.Package ? stable : rebuildable;

                if (!destination.TryAdd(simpleName, assembly.Path)
                    && destination[simpleName] != assembly.Path
                    && conflicting.Add(simpleName))
                {
                    diagnostics.Add(ProjectDiagnostic.ForProject(
                        ProjectDesignDiagnosticCodes.AssemblyNameConflict,
                        $"'{simpleName}' is both '{destination[simpleName]}' and '{assembly.Path}'. One name is one "
                            + "assembly in a generation, so the first is loaded, and a type only the second has is not found.",
                        ProjectDiagnosticSeverity.Warning,
                        project));
                }

                if (assembly.Origin is RuntimeAssemblyOrigin.Project or RuntimeAssemblyOrigin.ProjectReference
                    && !closure.Contains(simpleName, StringComparer.OrdinalIgnoreCase))
                {
                    closure.Add(simpleName);

                    if (assembly.Origin == RuntimeAssemblyOrigin.Project)
                    {
                        outputs.TryAdd(project, simpleName);
                    }
                    else if (!assembly.Project.IsEmpty && !projects.Contains(assembly.Project))
                    {
                        projects.Add(assembly.Project);
                    }
                }

                // After what the projects build, which the snapshot returns first, so a project's own
                // type still wins a name. Read once per file and set: the answer is the file's.
                if (assembly.Origin == RuntimeAssemblyOrigin.Package
                    && !closure.Contains(simpleName, StringComparer.OrdinalIgnoreCase)
                    && (xamlPackages.Contains(simpleName) || MapsXamlNamespace(assembly.Path)))
                {
                    closure.Add(simpleName);
                    xamlPackages.Add(simpleName);
                }
            }

            closures[project] = [.. closure];
        }

        // The process's copy wins for a name it has, and one Button type in the process is why. A
        // project building such a name is not what the designer can show, and is told so.
        foreach (Assembly loaded in AssemblyLoadContext.Default.Assemblies)
        {
            if (loaded.GetName().Name is { Length: > 0 } simpleName
                && rebuildable.TryGetValue(simpleName, out CanonicalPath shadowed))
            {
                diagnostics.Add(ProjectDiagnostic.ForFile(
                    ProjectDesignDiagnosticCodes.ShadowedByHost,
                    $"'{simpleName}' is an assembly this process already has, and its copy answers for the name: "
                        + $"the build at '{shadowed}' is not what the designer shows.",
                    ProjectDiagnosticSeverity.Warning,
                    shadowed));
            }
        }

        return (assemblies.ToImmutable(), new DesignSet(rebuildable, stable, closures, outputs, xamlPackages, diagnostics.ToImmutable()));
    }

    /// <summary>
    /// Whether a package's assembly maps a XAML namespace — declares Avalonia's
    /// <c>XmlnsDefinitionAttribute</c> — read from its metadata without loading it.
    /// </summary>
    /// <remarks>
    /// A package's assembly goes to the default context and stays there, so asking by loading would
    /// keep every restored package in the process for the question alone. A file that cannot be read
    /// maps nothing here; the document that names its namespace says which type it could not find.
    /// </remarks>
    private static bool MapsXamlNamespace(CanonicalPath path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path.Value);
            using var image = new PEReader(stream);

            if (!image.HasMetadata)
            {
                return false;
            }

            MetadataReader reader = image.GetMetadataReader();

            foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                // The attribute's type is Avalonia's, so in a package it is always a reference to
                // another assembly's type, through a member reference to its constructor.
                if (reader.GetCustomAttribute(handle).Constructor is { Kind: HandleKind.MemberReference } constructor
                    && reader.GetMemberReference((MemberReferenceHandle)constructor).Parent is { Kind: HandleKind.TypeReference } parent)
                {
                    TypeReference type = reader.GetTypeReference((TypeReferenceHandle)parent);

                    if (reader.StringComparer.Equals(type.Name, "XmlnsDefinitionAttribute")
                        && reader.StringComparer.Equals(type.Namespace, "Avalonia.Metadata"))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
        }

        return false;
    }

    /// <summary>
    /// Adds the full names of the types a document could name that an assembly on disk declares, read from
    /// its metadata without loading it.
    /// </summary>
    /// <remarks>
    /// What a predecessor could answer for in a successor's documents (<see cref="WaitForPredecessorsAsync"/>):
    /// the project's controls, views and models, public as a project declares them. Every assembly with
    /// compiled markup declares the same seven helpers of Avalonia's compiler — five internal, such as
    /// <c>CompiledAvaloniaXaml.XamlIlContext</c>, and two public whose names no document can write,
    /// <c>!XamlLoader</c> and <c>!AvaloniaResources</c> — and counting them, any two projects would be one
    /// another's predecessors. So a type counts when it is public, and so top level, and named as a document
    /// writes a name. Measured over thirty of ArxisStudio's own assemblies and the fixtures' build: those
    /// seven were the only top-level types any two of them shared. A file that cannot be read declares
    /// nothing here.
    /// </remarks>
    /// <param name="path">The assembly.</param>
    /// <param name="types">Where the names go.</param>
    private static void AddPublicTypes(CanonicalPath path, HashSet<string> types)
    {
        try
        {
            using FileStream stream = File.OpenRead(path.Value);
            using var image = new PEReader(stream);

            if (!image.HasMetadata)
            {
                return;
            }

            MetadataReader reader = image.GetMetadataReader();

            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);

                // Public is a top-level type's visibility; a nested type's is NestedPublic.
                if ((type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public
                    && reader.GetString(type.Namespace) is { Length: > 0 } space
                    && reader.GetString(type.Name) is { } name
                    && IsNameable(name))
                {
                    types.Add($"{space}.{name}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
        }
    }

    /// <summary>Whether a type's name is one a document can write: an identifier, and a generic type's arity after it.</summary>
    private static bool IsNameable(string name)
    {
        int arity = name.IndexOf('`', StringComparison.Ordinal);
        ReadOnlySpan<char> identifier = arity < 0 ? name : name.AsSpan(0, arity);

        if (identifier.IsEmpty || !(char.IsLetter(identifier[0]) || identifier[0] == '_'))
        {
            return false;
        }

        foreach (char character in identifier)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return arity < 0 || (arity < name.Length - 1 && !name.AsSpan(arity + 1).ContainsAnyExceptInRange('0', '9'));
    }

    /// <summary>Whether a package of this generation maps a XAML namespace, by its simple name.</summary>
    /// <param name="simpleName">The assembly's simple name.</param>
    /// <returns><see langword="true"/> when the package declares one.</returns>
    internal bool MapsXamlNamespace(string simpleName) => _xamlPackages.Contains(simpleName);

    /// <summary>
    /// The assemblies one project's documents may name: its own output, what it references, and the
    /// packages that map a XAML namespace, loaded by this generation.
    /// </summary>
    /// <remarks>
    /// What a type resolver for that project's documents searches. A form of a library does not see
    /// the application that references it, as its build does not. A package that maps a namespace is
    /// loaded the way packages are — into the default context, or the host's copy when it has one.
    /// </remarks>
    /// <param name="project">One of <see cref="Projects"/>, or a project they reference.</param>
    /// <returns>The assemblies, the project's own first; empty for a project this generation is not of.</returns>
    /// <exception cref="ObjectDisposedException">This context has been unloaded.</exception>
    public ImmutableArray<Assembly> AssembliesOf(ProjectIdentity project)
    {
        ObjectDisposedException.ThrowIf(IsUnloaded, this);

        if (!_closures.TryGetValue(project, out ImmutableArray<string> names))
        {
            return [];
        }

        ImmutableArray<Assembly>.Builder assemblies = ImmutableArray.CreateBuilder<Assembly>(names.Length);

        foreach (string name in names)
        {
            if (Resolve(new AssemblyName(name)) is { } assembly && !assemblies.Contains(assembly))
            {
                assemblies.Add(assembly);
            }
        }

        return assemblies.ToImmutable();
    }

    /// <summary>
    /// The assembly a project builds, as this generation loaded it — the one its documents'
    /// <c>x:Class</c> and private handlers live in.
    /// </summary>
    /// <param name="project">One of <see cref="Projects"/>.</param>
    /// <returns>The assembly, or <see langword="null"/> when the project's output was not there to load.</returns>
    /// <exception cref="ObjectDisposedException">This context has been unloaded.</exception>
    public Assembly? ResolveProjectAssembly(ProjectIdentity project)
    {
        ObjectDisposedException.ThrowIf(IsUnloaded, this);

        return _outputs.TryGetValue(project, out string? name) ? Resolve(new AssemblyName(name)) : null;
    }

    /// <summary>
    /// The packages this generation loaded into the default context, by name, and the files they came
    /// from.
    /// </summary>
    /// <remarks>
    /// Those stay for the life of the process whatever becomes of the generation, so a successor
    /// whose snapshot names another file for one of them — another version — cannot load it, and the
    /// host answers that with a restart.
    /// </remarks>
    internal IReadOnlyDictionary<string, CanonicalPath> PackagesLoaded
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, CanonicalPath>(_packagesLoaded, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Loads every assembly the projects build, so the generation is one build of each.</summary>
    private void LoadEverything()
    {
        foreach (string name in _rebuildable.Keys)
        {
            Resolve(new AssemblyName(name));
        }
    }

    /// <summary>
    /// Whether this context still describes the project as a snapshot has it.
    /// </summary>
    /// <remarks>
    /// The stale check, and it is deliberately about the whole workspace rather than the one
    /// project. A version advances on every publication, so this says "nothing has been re-read
    /// since" — which is the only thing that can be known cheaply and the only thing that is safe
    /// to act on. A host that wants to know whether <em>this project in particular</em> changed
    /// compares what it cares about itself; erring towards rebuilding a context is cheap, and
    /// showing a control built from a model two refreshes old is not.
    /// </remarks>
    /// <param name="snapshot">The snapshot to compare against.</param>
    /// <returns><see langword="true"/> when nothing has been published since this was built.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public bool IsCurrentFor(SolutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Version == Version;
    }

    /// <summary>
    /// Whether the rebuildable files on disk are still the ones this context was created over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other staleness question, asked after a build rather than after a refresh:
    /// <see cref="IsCurrentFor"/> says whether the <em>model</em> moved on, this says whether the
    /// <em>types</em> did. A build that had nothing to do rewrites nothing and the answer stays
    /// <see langword="true"/>; one that recompiled anything the project owns turns it
    /// <see langword="false"/>, which is a host's cue to reclaim this generation and build its
    /// successor — see <see cref="TryReclaimAsync"/> and <c>docs/adr/0023</c>. A restart is what
    /// happens when the reclaim answers <see langword="false"/>.
    /// </para>
    /// <para>
    /// Compared by write time and size against a stamp taken at creation, which answers "did a
    /// build produce new output since this generation was made" without opening anything. Only
    /// the rebuildable assemblies are consulted: a package file changing is not a build, and is
    /// recorded in the limitations as needing a new process regardless.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when no rebuildable file has changed since creation.</returns>
    public bool IsCurrentOnDisk()
    {
        // Both dictionaries are built together in the constructor and neither is written again,
        // so every rebuildable assembly has a stamp and one walk answers for both.
        foreach (KeyValuePair<string, CanonicalPath> assembly in _rebuildable)
        {
            if (!_stamps[assembly.Key].Equals(FileStamp.Of(assembly.Value.Value)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What a file looked like from outside, enough to notice it being rewritten.</summary>
    private readonly record struct FileStamp(bool Exists, long Length, DateTime LastWriteUtc)
    {
        public static FileStamp Of(string path)
        {
            try
            {
                var file = new FileInfo(path);

                return file.Exists
                    ? new FileStamp(true, file.Length, file.LastWriteTimeUtc)
                    : new FileStamp(false, 0, default);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be examined is treated as absent; if it becomes readable
                // later, the comparison will say so, which errs towards reloading.
                return new FileStamp(false, 0, default);
            }
        }
    }

    private static Dictionary<string, FileStamp> Stamp(Dictionary<string, CanonicalPath> rebuildable)
    {
        var stamps = new Dictionary<string, FileStamp>(rebuildable.Count, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, CanonicalPath> assembly in rebuildable)
        {
            stamps.Add(assembly.Key, FileStamp.Of(assembly.Value.Value));
        }

        return stamps;
    }

    /// <summary>
    /// Finds an assembly by name, loading it if this context is responsible for it.
    /// </summary>
    /// <remarks>
    /// Both hits and misses are remembered. An assembly cannot be loaded twice, and a name already
    /// known to be absent will still be absent the next time a document mentions it.
    /// </remarks>
    /// <param name="assemblyName">The name to resolve.</param>
    /// <returns>The assembly, or <see langword="null"/> when nothing here knows the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assemblyName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This context has been unloaded.</exception>
    public Assembly? Resolve(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);
        ObjectDisposedException.ThrowIf(IsUnloaded, this);

        string simpleName = assemblyName.Name ?? string.Empty;

        if (simpleName.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_resolved.TryGetValue(simpleName, out Assembly? cached))
            {
                return cached;
            }

            Assembly? assembly = Load(simpleName);

            _resolved[simpleName] = assembly;

            return assembly;
        }
    }

    /// <summary>
    /// Puts the process's runtime XAML compiler into this context for the length of a load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Avalonia's runtime XAML compiler keeps one reflection-emit type system for the whole process:
    /// created on the first load, never replaced, and remembering every assembly by simple name.
    /// The generated code references the document's types through it — so after a rebuild, a class
    /// that exists only in the new build is compiled against the copy the process saw first, and
    /// creating the object fails with <c>Could not load type</c> naming an assembly that plainly
    /// contains the type. The designer's whole reason for collectible contexts is that a rebuilt
    /// assembly is a different assembly; the compiler's cache is the one place that disagreed.
    /// </para>
    /// <para>
    /// So a load runs inside this scope. Entering it does two things: if the compiler's emitted
    /// state lives in some other context — an older generation, or the default — that state is
    /// reset, so the next load rebuilds it from scratch; and contextual reflection is entered on
    /// this context, so the rebuilt dynamic assembly is created <em>inside</em> it and every
    /// assembly reference the generated code makes binds here first. Two open forms from two
    /// generations each re-enter their own context, at the cost of the compiler re-initialising
    /// when the generation actually changes.
    /// </para>
    /// <para>
    /// The reset reaches into the compiler's non-public state by name, which is as fragile as it
    /// sounds and is recorded as such: if a future Avalonia renames the fields, the reset quietly
    /// does nothing and the pre-existing behaviour returns — stale, but not broken in any new way.
    /// See <c>docs/adr/0020-the-adapter-resets-avalonias-runtime-xaml-compiler.md</c>.
    /// </para>
    /// <para>
    /// This is also the context's <see cref="ArxisStudio.Markup.Xaml.Loader.IXamlCompilationScope"/>
    /// implementation: every <c>ProjectXamlEnvironment.Create</c> hands the context to the
    /// environment, and the session then enters this around every compilation on its own. Nothing
    /// needs to call it by hand.
    /// </para>
    /// <para>
    /// <b>A generation that loaded nothing</b> — a design set whose builds are not there, because the
    /// first design build failed — does not take the compiler in. The runtime never unloads a collectible
    /// context whose only assembly is a dynamic one: emitted there, the compiler's assembly would keep the
    /// generation in the process for good, and the first successful build would end in a restart. Its
    /// documents name nothing of the project, so they compile against the process — and the compiler's
    /// state is let go of when the scope ends, because state left in the default context outlives every
    /// generation and holds whatever the next one loads. Measured, and recorded in ADR 0020.
    /// </para>
    /// </remarks>
    /// <returns>The scope to dispose when the load is done.</returns>
    /// <exception cref="ObjectDisposedException">This context has been unloaded.</exception>
    public IDisposable EnterLoadScope()
    {
        ObjectDisposedException.ThrowIf(IsUnloaded, this);

        AssemblyLoadContext context = _context
            ?? throw new ObjectDisposedException(nameof(ProjectAssemblyContext));

        if (!context.Assemblies.Any())
        {
            RuntimeXamlCompiler.EnsureEmittedIn(AssemblyLoadContext.Default);

            return ProcessScope.Instance;
        }

        RuntimeXamlCompiler.EnsureEmittedIn(context);

        return new LoadScope(context.EnterContextualReflection());
    }

    /// <inheritdoc />
    IDisposable ArxisStudio.Markup.Xaml.Loader.IXamlCompilationScope.Enter() => EnterLoadScope();

    private sealed class LoadScope(AssemblyLoadContext.ContextualReflectionScope scope) : IDisposable
    {
        public void Dispose() => scope.Dispose();
    }

    /// <summary>
    /// The scope of a generation that loaded nothing: its markup compiles in the default context, and the
    /// compiler is let go of on the way out.
    /// </summary>
    private sealed class ProcessScope : IDisposable
    {
        public static ProcessScope Instance { get; } = new();

        public void Dispose() => RuntimeXamlCompiler.ResetIfEmittedIn(AssemblyLoadContext.Default);
    }

    /// <summary>The runtime compiler's static state, reached the only way it can be.</summary>
    private static class RuntimeXamlCompiler
    {
        private static readonly Lock Sync = new();

        private static readonly Type? Compiler = Type.GetType(
            "Avalonia.Markup.Xaml.XamlIl.AvaloniaXamlIlRuntimeCompiler, Avalonia.Markup.Xaml.Loader",
            throwOnError: false);

        /// <summary>
        /// Every piece of emitted state, of which <c>_sreAsm</c> says where it lives.
        /// </summary>
        /// <remarks>
        /// The prefix catches seven of the eight statics Avalonia 12.1.1 declares; the eighth is
        /// <c>_ignoresAccessChecksFromAttribute</c>, a type emitted into the dynamic assembly and
        /// therefore as generation-bound as anything named <c>_sre</c>. Missing it left a type from
        /// a possibly-collected assembly wired into every later emit.
        /// </remarks>
        private static readonly System.Reflection.FieldInfo[] State = Compiler is null
            ? []
            : [.. System.Linq.Enumerable.Where(
                Compiler.GetFields(
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic),
                field => (field.Name.StartsWith("_sre", StringComparison.Ordinal)
                        || field.Name == "_ignoresAccessChecksFromAttribute")
                    && !field.FieldType.IsValueType)];

        internal static void EnsureEmittedIn(AssemblyLoadContext context)
        {
            lock (Sync)
            {
                foreach (System.Reflection.FieldInfo field in State)
                {
                    if (field.Name == "_sreAsm" && field.GetValue(null) is Assembly emitted)
                    {
                        if (ReferenceEquals(AssemblyLoadContext.GetLoadContext(emitted), context))
                        {
                            return;
                        }

                        Reset();

                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Clears the emitted state when it belongs to a context that is going away.
        /// </summary>
        /// <remarks>
        /// The mirror of <see cref="EnsureEmittedIn"/>, for the other moment: nothing has entered
        /// a successor's scope yet, so the compiler's own dynamic assembly is still the dying
        /// generation's and roots it all by itself.
        /// </remarks>
        internal static void ResetIfEmittedIn(AssemblyLoadContext dying)
        {
            lock (Sync)
            {
                foreach (System.Reflection.FieldInfo field in State)
                {
                    if (field.Name == "_sreAsm" && field.GetValue(null) is Assembly emitted)
                    {
                        if (ReferenceEquals(AssemblyLoadContext.GetLoadContext(emitted), dying))
                        {
                            Reset();
                        }

                        return;
                    }
                }
            }
        }

        private static void Reset()
        {
            foreach (System.Reflection.FieldInfo field in State)
            {
                field.SetValue(null, null);
            }
        }
    }

    /// <summary>Unloads the context, so a rebuild of the same project can be loaded next.</summary>
    /// <remarks>
    /// Asks; it cannot insist. The runtime frees a collectible context only once nothing refers to
    /// anything inside it, so a host still holding a control built from these assemblies keeps them
    /// alive — which is correct, and is why disposal returns rather than waits.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AssemblyLoadContext? context;

        lock (_gate)
        {
            _resolved.Clear();

            context = _context;

            // Let go of it here, or this object — which the host is still holding while it asks
            // whether the generation went — would be the thing keeping it.
            _context = null;
        }

        context?.Unload();
    }

    private static string Describe(SolutionSnapshot snapshot, ProjectIdentity project) =>
        snapshot.TryGetProject(project, out ProjectSnapshot? found)
            ? $"{found.Name} @{snapshot.Version}"
            : $"{project} @{snapshot.Version}";

    private static string Describe(SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> set) =>
        string.Join('+', set.Select(project =>
            snapshot.TryGetProject(project, out ProjectSnapshot? found) ? found.Name : project.ToString()))
            + $" @{snapshot.Version}";

    private Assembly? Load(string simpleName)
    {
        // The host's copy wins for anything it already has, which keeps one Avalonia in the process
        // and therefore one Avalonia.Controls.Button.
        foreach (Assembly loaded in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        if (_rebuildable.TryGetValue(simpleName, out CanonicalPath rebuildable))
        {
            return LoadWithoutHoldingTheFile(rebuildable);
        }

        // A package file is not going to be rewritten under us, so it can be loaded the ordinary
        // way -- and into the default context, so that everything sharing it sees one copy.
        if (_stable.TryGetValue(simpleName, out CanonicalPath stable) && File.Exists(stable.Value))
        {
            try
            {
                Assembly package = Assembly.LoadFrom(stable.Value);

                _packagesLoaded[simpleName] = stable;

                return package;
            }
            catch (Exception exception) when (IsUnloadable(exception))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the file and loads the bytes, so the build that produces the next version of it can
    /// overwrite it.
    /// </summary>
    /// <remarks>
    /// Symbols are loaded alongside when they are there, because a stack trace without line numbers
    /// is most of the value of a designer's error report gone.
    /// </remarks>
    private Assembly? LoadWithoutHoldingTheFile(CanonicalPath path)
    {
        if (_context is not { } context || !File.Exists(path.Value))
        {
            return null;
        }

        try
        {
            byte[] assembly = File.ReadAllBytes(path.Value);
            string symbolsPath = Path.ChangeExtension(path.Value, ".pdb");

            using var assemblyStream = new MemoryStream(assembly);

            if (File.Exists(symbolsPath))
            {
                using var symbolStream = new MemoryStream(File.ReadAllBytes(symbolsPath));

                return context.LoadFromStream(assemblyStream, symbolStream);
            }

            return context.LoadFromStream(assemblyStream);
        }
        catch (Exception exception) when (IsUnloadable(exception))
        {
            // A file that is not a usable assembly is a miss rather than a crash. The snapshot said
            // a build would put one there; it does not promise the build succeeded.
            return null;
        }
    }

    private static bool IsUnloadable(Exception exception) =>
        exception is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException;
}

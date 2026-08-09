using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
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
/// Nothing here decides <em>when</em> to unload. That is the host's, because only the host knows
/// whether anything is still looking at the old objects.
/// </para>
/// </remarks>
public sealed class ProjectAssemblyContext : IDisposable
{
    private readonly AssemblyLoadContext _context;
    private readonly Dictionary<string, CanonicalPath> _rebuildable;
    private readonly Dictionary<string, CanonicalPath> _stable;
    private readonly Dictionary<string, Assembly?> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private int _disposed;

    private ProjectAssemblyContext(
        string name,
        ProjectIdentity project,
        WorkspaceVersion version,
        ImmutableArray<RuntimeAssemblyReference> assemblies,
        Dictionary<string, CanonicalPath> rebuildable,
        Dictionary<string, CanonicalPath> stable)
    {
        Name = name;
        Project = project;
        Version = version;
        Assemblies = assemblies;

        _rebuildable = rebuildable;
        _stable = stable;
        _context = new AssemblyLoadContext(name, isCollectible: true);

        // The hook fires for a dependency the loader could not satisfy itself, which is how a
        // referenced project's output is found without anybody naming it up front.
        _context.Resolving += (_, name) => Resolve(name);
    }

    /// <summary>Gets the name this context was created with, which shows up in diagnostics.</summary>
    public string Name { get; }

    /// <summary>Gets the project these assemblies belong to.</summary>
    public ProjectIdentity Project { get; }

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

    /// <summary>Gets what the project needs, in the order a resolver should consult it.</summary>
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
    /// <returns>The context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static ProjectAssemblyContext Create(
        SolutionSnapshot snapshot, ProjectIdentity project, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ImmutableArray<RuntimeAssemblyReference> assemblies = snapshot.GetRuntimeAssemblies(project);

        var rebuildable = new Dictionary<string, CanonicalPath>(StringComparer.OrdinalIgnoreCase);
        var stable = new Dictionary<string, CanonicalPath>(StringComparer.OrdinalIgnoreCase);

        foreach (RuntimeAssemblyReference assembly in assemblies)
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

            // First one wins, which is why the snapshot returns them in priority order.
            Dictionary<string, CanonicalPath> destination =
                assembly.Origin == RuntimeAssemblyOrigin.Package ? stable : rebuildable;

            destination.TryAdd(simpleName, assembly.Path);
        }

        string label = name ?? Describe(snapshot, project);

        return new ProjectAssemblyContext(
            label, project, snapshot.Version, assemblies, rebuildable, stable);
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
    /// </remarks>
    /// <returns>The scope to dispose when the load is done.</returns>
    /// <exception cref="ObjectDisposedException">This context has been unloaded.</exception>
    public IDisposable EnterLoadScope()
    {
        ObjectDisposedException.ThrowIf(IsUnloaded, this);

        RuntimeXamlCompiler.EnsureEmittedIn(_context);

        return new LoadScope(_context.EnterContextualReflection());
    }

    private sealed class LoadScope(AssemblyLoadContext.ContextualReflectionScope scope) : IDisposable
    {
        public void Dispose() => scope.Dispose();
    }

    /// <summary>The runtime compiler's static state, reached the only way it can be.</summary>
    private static class RuntimeXamlCompiler
    {
        private static readonly Lock Sync = new();

        private static readonly Type? Compiler = Type.GetType(
            "Avalonia.Markup.Xaml.XamlIl.AvaloniaXamlIlRuntimeCompiler, Avalonia.Markup.Xaml.Loader",
            throwOnError: false);

        /// <summary>Every piece of emitted state, of which <c>_sreAsm</c> says where it lives.</summary>
        private static readonly System.Reflection.FieldInfo[] State = Compiler is null
            ? []
            : [.. System.Linq.Enumerable.Where(
                Compiler.GetFields(
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic),
                field => field.Name.StartsWith("_sre", StringComparison.Ordinal)
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

        lock (_gate)
        {
            _resolved.Clear();
        }

        _context.Unload();
    }

    private static string Describe(SolutionSnapshot snapshot, ProjectIdentity project) =>
        snapshot.TryGetProject(project, out ProjectSnapshot? found)
            ? $"{found.Name} @{snapshot.Version}"
            : $"{project} @{snapshot.Version}";

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
                return Assembly.LoadFrom(stable.Value);
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
        if (!File.Exists(path.Value))
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

                return _context.LoadFromStream(assemblyStream, symbolStream);
            }

            return _context.LoadFromStream(assemblyStream);
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

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia.Controls;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>What a project's documents can place, and making a control placeable.</summary>
public sealed partial class ProjectDesignHost
{
    private TaskCompletionSource _stateMoved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The controls a project's documents can place: what the live generation built of the project and the
    /// projects it references, and the documents among them that declare a class it has not built yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A control is what a document writes as an element and lays out: creatable, a control, and not a
    /// window, which stands inside nothing. The built ones are read from the live generation by name
    /// (<see cref="XamlTypeCatalog"/>), holding nothing of it. A document of those projects whose
    /// <c>x:Class</c> names a class the generation does not have — the IDE has written it and nobody has
    /// built it — is listed as not built, written in <c>using:</c> its namespace, unless its root is a
    /// window; <see cref="EnsureBuiltAsync"/> is what placing it takes. An open document's text outranks
    /// its file.
    /// </para>
    /// <para>
    /// The listing is taken in a turn, so it is of one generation and not between two. It is a reading,
    /// not a view: a build that adds a control is a new listing.
    /// </para>
    /// </remarks>
    /// <param name="project">The project whose documents place the controls.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>
    /// The controls, built ones first and each in name order; empty for a project outside the design set
    /// or a host with no generation.
    /// </returns>
    /// <exception cref="InvalidOperationException">The host has not been started.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async ValueTask<ImmutableArray<ProjectControlInfo>> GetPlaceableControlsAsync(
        ProjectIdentity project,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        SolutionSnapshot snapshot = StartedSnapshot();
        ImmutableArray<ProjectIdentity> closure = ClosureOf(snapshot, DesignSetOf(snapshot), project);

        if (closure.IsEmpty)
        {
            return [];
        }

        ImmutableArray<ProjectControlInfo> controls = [];

        await InTurnAsync(
            async () =>
            {
                Dictionary<string, DeclaredClass> declared = await DeclaredClassesAsync(snapshot, closure, cancellationToken).ConfigureAwait(false);
                ImmutableArray<ProjectControlInfo> built = BuiltControls(closure, declared);
                var builtNames = new HashSet<string>(built.Select(static control => control.ClassName), StringComparer.Ordinal);
                var unbuilt = new List<ProjectControlInfo>();

                foreach ((string className, DeclaredClass declaration) in declared)
                {
                    if (!builtNames.Contains(className)
                        && await UnbuiltAsync(className, declaration, cancellationToken).ConfigureAwait(false) is { } control)
                    {
                        unbuilt.Add(control);
                    }
                }

                controls = [.. built, .. unbuilt.OrderBy(static control => control.Name, StringComparer.Ordinal)];
            },
            cancellationToken).ConfigureAwait(false);

        return controls;
    }

    /// <summary>
    /// Makes a control placeable: builds its project when the live generation does not have its class,
    /// and waits for the generation that does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A control the live generation has is placeable as it is, and nothing is built. Otherwise its
    /// project is built as any build of the host is — the top projects of what references it — and the
    /// generation the build calls for replaces the live one as soon as nothing holds the swap off. This
    /// waits for that and never swaps past a deferral: what holds a swap off is somebody in the middle of
    /// something, and placing a control is not more urgent than finishing it.
    /// </para>
    /// <para>
    /// The answer is whether the live generation has the class afterwards. A build that failed, a class
    /// the build did not produce, and a generation that would not go are all <see langword="false"/>;
    /// <see cref="BuildCompleted"/> and <see cref="RestartRequired"/> say which.
    /// </para>
    /// </remarks>
    /// <param name="control">The control, as <see cref="GetPlaceableControlsAsync"/> listed it.</param>
    /// <param name="cancellationToken">A token to give up waiting with; a build begun is not stopped by it.</param>
    /// <returns>Whether the live generation has the control's class.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="control"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The host has not been started.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async ValueTask<bool> EnsureBuiltAsync(ProjectControlInfo control, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        ThrowIfDisposed();

        StartedSnapshot();

        if (HasClass(control))
        {
            return true;
        }

        ProjectDesignBuildResult built = await BuildAsync($"{control.Name} is being placed", [control.Project], cancellationToken)
            .ConfigureAwait(false);

        if (built.Status != ProjectOperationStatus.Succeeded)
        {
            return false;
        }

        await UntilSettledAsync(cancellationToken).ConfigureAwait(false);

        return HasClass(control);
    }

    /// <summary>
    /// Waits until the generation is what the builds have made it — no build, no swap waiting or running
    /// — or until the host cannot get there, because it requires a restart or was disposed.
    /// </summary>
    private async Task UntilSettledAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task moved;

            lock (_sync)
            {
                if (ComputeState() is ProjectDesignState.Live or ProjectDesignState.RestartRequired or ProjectDesignState.Disposed)
                {
                    return;
                }

                moved = _stateMoved.Task;
            }

            await moved.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Wakes whoever waits for the state to move. Called once it did, outside the lock.</summary>
    private void SignalStateMoved()
    {
        TaskCompletionSource moved;

        lock (_sync)
        {
            moved = _stateMoved;
            _stateMoved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        moved.TrySetResult();
    }

    /// <summary>Whether the live generation has a control's class, holding nothing of it afterwards.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool HasClass(ProjectControlInfo control)
    {
        ProjectAssemblyContext? generation;

        lock (_sync)
        {
            generation = _generation;
        }

        try
        {
            return generation is { IsUnloaded: false }
                && generation.ResolveProjectAssembly(control.Project)?.GetType(control.ClassName, throwOnError: false) is not null;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A project and the projects it references, as far as the design set reaches, in snapshot order —
    /// what that project's documents can name.
    /// </summary>
    private static ImmutableArray<ProjectIdentity> ClosureOf(
        SolutionSnapshot snapshot,
        ImmutableArray<ProjectIdentity> set,
        ProjectIdentity project)
    {
        var inSet = new HashSet<ProjectIdentity>(set);

        if (!inSet.Contains(project))
        {
            return [];
        }

        var reached = new HashSet<ProjectIdentity> { project };
        var queue = new Queue<ProjectIdentity>([project]);

        while (queue.Count > 0)
        {
            if (!snapshot.TryGetProject(queue.Dequeue(), out ProjectSnapshot? current))
            {
                continue;
            }

            foreach (ProjectReferenceInfo reference in current.ProjectReferences)
            {
                if (!reference.Project.IsEmpty && inSet.Contains(reference.Project) && reached.Add(reference.Project))
                {
                    queue.Enqueue(reference.Project);
                }
            }
        }

        return [.. set.Where(reached.Contains)];
    }

    /// <summary>
    /// The classes the closure's documents declare, by full name: which project, which file, and the
    /// root it is written as. An open document's text outranks its file; a file that cannot be read now
    /// is left for the next listing.
    /// </summary>
    private async Task<Dictionary<string, DeclaredClass>> DeclaredClassesAsync(
        SolutionSnapshot snapshot,
        ImmutableArray<ProjectIdentity> closure,
        CancellationToken cancellationToken)
    {
        var declared = new Dictionary<string, DeclaredClass>(StringComparer.Ordinal);

        foreach (ProjectIdentity identity in closure)
        {
            if (!snapshot.TryGetProject(identity, out ProjectSnapshot? project))
            {
                continue;
            }

            foreach (ProjectItem item in project.Items)
            {
                if (!IsMarkupItem(item) || item.FullPath.IsEmpty || !item.FullPath.StartsWith(project.ProjectDirectory))
                {
                    continue;
                }

                XamlDocument? document = OpenDocumentOn(item.FullPath);

                if (document is null)
                {
                    try
                    {
                        SourceText text = await ReadTextAsync(item.FullPath, cancellationToken).ConfigureAwait(false);

                        document = XamlDocument.Parse(text, new XamlParseOptions { DocumentUri = UriOf(snapshot, item.FullPath) });
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                }

                if (document.Root is { } root && root.GetDirective(XamlDirectives.Class) is { Length: > 0 } className)
                {
                    declared.TryAdd(className, new DeclaredClass(identity, item.FullPath, root));
                }
            }
        }

        return declared;
    }

    /// <summary>The text of the document open on a file, when one is.</summary>
    private XamlDocument? OpenDocumentOn(CanonicalPath file)
    {
        lock (_sync)
        {
            return FindFile(file)?.Live.Document;
        }
    }

    /// <summary>
    /// What the live generation built of the closure that a document can place, by name — holding
    /// nothing of the generation once it has answered.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ImmutableArray<ProjectControlInfo> BuiltControls(
        ImmutableArray<ProjectIdentity> closure,
        Dictionary<string, DeclaredClass> declared)
    {
        ProjectAssemblyContext? generation;

        lock (_sync)
        {
            generation = _generation;
        }

        if (generation is not { IsUnloaded: false })
        {
            return [];
        }

        var controls = new List<ProjectControlInfo>();

        try
        {
            foreach (ProjectIdentity project in closure)
            {
                if (generation.ResolveProjectAssembly(project) is not { } assembly)
                {
                    continue;
                }

                foreach (XamlTypeEntry entry in XamlTypeCatalog.Create([assembly]).Entries)
                {
                    if (IsPlaceable(entry.Kinds))
                    {
                        controls.Add(new ProjectControlInfo(
                            entry.FullName,
                            entry.Name,
                            entry.XmlNamespace,
                            entry.SuggestedPrefix,
                            entry.Kinds,
                            project,
                            declared.TryGetValue(entry.FullName, out DeclaredClass? declaration) ? declaration.Document : default,
                            IsBuilt: true));
                    }
                }
            }
        }
        catch (ObjectDisposedException)
        {
            return [];
        }

        return [.. controls.OrderBy(static control => control.Name, StringComparer.Ordinal).ThenBy(static control => control.ClassName, StringComparer.Ordinal)];
    }

    /// <summary>Whether a document can write a type as an element and lay it out.</summary>
    private static bool IsPlaceable(XamlTypeKinds kinds) =>
        (kinds & (XamlTypeKinds.Creatable | XamlTypeKinds.Control)) == (XamlTypeKinds.Creatable | XamlTypeKinds.Control)
        && (kinds & XamlTypeKinds.TopLevel) == 0;

    /// <summary>
    /// A class a document declares and the live generation has not built, as a control — or nothing,
    /// when its root says it will be a window.
    /// </summary>
    /// <remarks>
    /// The root is resolved in the environment of the project the document is in, so a window of the
    /// project's own base class is a window too; a root that does not resolve says nothing either way,
    /// and the class is listed.
    /// </remarks>
    private async Task<ProjectControlInfo?> UnbuiltAsync(string className, DeclaredClass declaration, CancellationToken cancellationToken)
    {
        XamlTypeKinds kinds = XamlTypeKinds.Creatable | XamlTypeKinds.Control | XamlTypeKinds.CompiledMarkup;
        ProjectAssemblyContext? generation;

        lock (_sync)
        {
            generation = _generation;
        }

        if (generation is { IsUnloaded: false })
        {
            try
            {
                XamlTypeResolution resolution = await EnvironmentOf(generation, declaration.Project).TypeResolver.ResolveAsync(
                    new XamlTypeName(declaration.Root.NamespaceUri ?? string.Empty, declaration.Root.Name.LocalName),
                    declaration.Root.NamespaceContext,
                    cancellationToken).ConfigureAwait(false);

                switch (KindOf(resolution))
                {
                    case RootKind.Window:
                        return null;

                    case RootKind.UserControl:
                        kinds |= XamlTypeKinds.UserControl | XamlTypeKinds.ContentControl | XamlTypeKinds.TemplatedControl;
                        break;
                }
            }
            catch (ObjectDisposedException)
            {
                // Replaced while asked; the root says nothing, and the class is listed.
            }
        }

        int dot = className.LastIndexOf('.');

        return new ProjectControlInfo(
            className,
            dot < 0 ? className : className[(dot + 1)..],
            "using:" + (dot < 0 ? string.Empty : className[..dot]),
            SuggestedPrefix: null,
            kinds,
            declaration.Project,
            declaration.Document,
            IsBuilt: false);
    }

    /// <summary>What a resolved root makes a class, without keeping the type.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RootKind KindOf(XamlTypeResolution resolution) =>
        resolution.Type is not { } type ? RootKind.Unknown
        : typeof(TopLevel).IsAssignableFrom(type) ? RootKind.Window
        : typeof(UserControl).IsAssignableFrom(type) ? RootKind.UserControl
        : RootKind.Unknown;

    private enum RootKind
    {
        Unknown,
        Window,
        UserControl,
    }

    /// <summary>A class a document declares: the project it is in, the file, and the root it is written as.</summary>
    private sealed record DeclaredClass(ProjectIdentity Project, CanonicalPath Document, XamlElement Root);
}

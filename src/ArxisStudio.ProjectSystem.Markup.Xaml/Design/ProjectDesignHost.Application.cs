using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>A form's application: the styles and resources the program dresses it in (ADR 0033).</summary>
public sealed partial class ProjectDesignHost
{
    private const string ConventionalApplication = "App.axaml";

    // Every application opened and not closed yet: a swap closes what the designer left open.
    private readonly List<ProjectDesignApplication> _applications = [];

    // Which document is each project's application, for the projects somebody asked about; null — it has
    // none. Forgotten whenever a snapshot is published or markup is saved: either can make a document one.
    private readonly Dictionary<ProjectIdentity, CanonicalPath?> _applicationFiles = [];

    /// <summary>
    /// Raised on the user interface thread when an application document an open application was loaded
    /// from was saved. Close the application and open it again.
    /// </summary>
    public event EventHandler<ProjectDesignApplicationEventArgs>? ApplicationChanged;

    /// <summary>
    /// Loads what a document's application declares — its styles, resources and data templates — for that
    /// document's form alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application is the document project's own <c>App.axaml</c> — any of its markup whose root is an
    /// application — or, for a library with none, that of the first project of the design set that
    /// references it, in snapshot order: the program the library's forms are shown in. It is loaded in the
    /// live generation, as written: the program's own <c>App</c> class is never constructed.
    /// </para>
    /// <para>
    /// Each call loads the document again, because a form takes what it is given over: Avalonia gives a
    /// style and a dictionary one owner (see <see cref="ProjectDesignApplication"/>). Answers
    /// <see langword="null"/> when the document's project has no application, and when there is no live
    /// generation to load in — while the host requires a restart.
    /// </para>
    /// </remarks>
    /// <param name="document">A document the host opened.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The application, or <see langword="null"/> when there is none to load.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The host did not open the document, or has closed it.</exception>
    /// <exception cref="InvalidOperationException">The host has not been started.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    /// <exception cref="IOException">The application's document could not be read.</exception>
    public async ValueTask<ProjectDesignApplication?> OpenApplicationAsync(
        XamlLiveDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThrowIfDisposed();

        // In a turn, so the generation the application is loaded in is not replaced while it is.
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ProjectIdentity project;
            ProjectAssemblyContext? generation;

            lock (_sync)
            {
                project = (Find(document)
                    ?? throw new ArgumentException("The design host did not open this document, or has closed it.", nameof(document))).Project;
                generation = _generation;
            }

            SolutionSnapshot snapshot = StartedSnapshot();

            if (generation is not { IsUnloaded: false }
                || await ApplicationOfAsync(snapshot, generation, project, cancellationToken).ConfigureAwait(false) is not { } found)
            {
                return null;
            }

            return await LoadApplicationAsync(snapshot, generation, found.File, found.Project, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>The application document a project's forms are shown with, and the project it belongs to.</summary>
    private async Task<(CanonicalPath File, ProjectIdentity Project)?> ApplicationOfAsync(
        SolutionSnapshot snapshot,
        ProjectAssemblyContext generation,
        ProjectIdentity project,
        CancellationToken cancellationToken)
    {
        foreach (ProjectIdentity candidate in ApplicationCandidates(snapshot, project))
        {
            if (await ApplicationDocumentOfAsync(snapshot, generation, candidate, cancellationToken).ConfigureAwait(false) is { } file)
            {
                return (file, candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// The project itself, then the projects of the design set that reference it — directly or through
    /// others — in snapshot order.
    /// </summary>
    internal static IEnumerable<ProjectIdentity> ApplicationCandidates(SolutionSnapshot snapshot, ProjectIdentity project)
    {
        yield return project;

        foreach (ProjectIdentity other in DesignSetOf(snapshot))
        {
            if (other != project && References(snapshot, other, project))
            {
                yield return other;
            }
        }
    }

    /// <summary>Whether one project references another, directly or through the projects it references.</summary>
    private static bool References(SolutionSnapshot snapshot, ProjectIdentity from, ProjectIdentity to)
    {
        var seen = new HashSet<ProjectIdentity> { from };
        var queue = new Queue<ProjectIdentity>([from]);

        while (queue.Count > 0)
        {
            if (!snapshot.TryGetProject(queue.Dequeue(), out ProjectSnapshot? current))
            {
                continue;
            }

            foreach (ProjectReferenceInfo reference in current.ProjectReferences)
            {
                if (reference.Project.IsEmpty)
                {
                    continue;
                }

                if (reference.Project == to)
                {
                    return true;
                }

                if (seen.Add(reference.Project))
                {
                    queue.Enqueue(reference.Project);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The markup of a project whose root is an application, remembered until a snapshot is published or
    /// markup is saved. <c>App.axaml</c> is read first, the convention every template follows.
    /// </summary>
    private async Task<CanonicalPath?> ApplicationDocumentOfAsync(
        SolutionSnapshot snapshot,
        ProjectAssemblyContext generation,
        ProjectIdentity project,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_applicationFiles.TryGetValue(project, out CanonicalPath? known))
            {
                return known;
            }
        }

        CanonicalPath? found = null;

        if (snapshot.TryGetProject(project, out ProjectSnapshot? declaring))
        {
            IEnumerable<ProjectItem> markup = declaring.Items
                .Where(static item => IsMarkupItem(item) && !item.FullPath.IsEmpty)
                .OrderBy(static item => item.FullPath.FileName.Equals(ConventionalApplication, StringComparison.OrdinalIgnoreCase) ? 0 : 1);

            foreach (ProjectItem item in markup)
            {
                XamlDocument? parsed = OpenDocumentOn(item.FullPath)
                    ?? await TryParseAsync(snapshot, item.FullPath, cancellationToken).ConfigureAwait(false);

                if (parsed?.Root is { } root
                    && await IsApplicationAsync(generation, project, root, cancellationToken).ConfigureAwait(false))
                {
                    found = item.FullPath;

                    break;
                }
            }
        }

        lock (_sync)
        {
            _applicationFiles[project] = found;
        }

        return found;
    }

    /// <summary>A file parsed as markup, or nothing when it cannot be read now.</summary>
    private static async Task<XamlDocument?> TryParseAsync(SolutionSnapshot snapshot, CanonicalPath file, CancellationToken cancellationToken)
    {
        try
        {
            SourceText text = await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);

            return XamlDocument.Parse(text, new XamlParseOptions { DocumentUri = UriOf(snapshot, file) });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a root resolves, in its project's environment, to an application.</summary>
    private async Task<bool> IsApplicationAsync(
        ProjectAssemblyContext generation,
        ProjectIdentity project,
        XamlElement root,
        CancellationToken cancellationToken)
    {
        XamlTypeResolution resolution = await EnvironmentOf(generation, project).TypeResolver.ResolveAsync(
            new XamlTypeName(root.NamespaceUri ?? string.Empty, root.Name.LocalName),
            root.NamespaceContext,
            cancellationToken).ConfigureAwait(false);

        return IsApplication(resolution);
    }

    /// <summary>Whether a resolution is of an application, without keeping the type.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsApplication(XamlTypeResolution resolution) =>
        resolution.Type is { } type && typeof(Application).IsAssignableFrom(type);

    /// <summary>Loads an application document as written, in its project's environment, and keeps it among the open ones.</summary>
    private async Task<ProjectDesignApplication> LoadApplicationAsync(
        SolutionSnapshot snapshot,
        ProjectAssemblyContext generation,
        CanonicalPath file,
        ProjectIdentity project,
        CancellationToken cancellationToken)
    {
        SourceText text = await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
        XamlDocument parsed = XamlDocument.Parse(text, new XamlParseOptions { DocumentUri = UriOf(snapshot, file) });

        XamlLoadOptions defaults = ProjectXamlEnvironment.CreateOptions(generation, project, XamlLoadMode.Design);

        var options = new XamlLoadOptions
        {
            Mode = defaults.Mode,
            LocalAssembly = defaults.LocalAssembly,
            UseCompiledBindingsByDefault = defaults.UseCompiledBindingsByDefault,
            ClassUse = XamlClassUse.AsWritten,
        };

        (XamlLoadSession? session, XamlLoadResult result) = await XamlLoadSession
            .TryCreateAsync(parsed, EnvironmentOf(generation, project), options, cancellationToken)
            .ConfigureAwait(false);

        var application = new ProjectDesignApplication(this, session, file, project, result.Diagnostics);

        lock (_sync)
        {
            _applications.Add(application);
        }

        return application;
    }

    /// <summary>Takes a closed application off the open ones.</summary>
    internal void Forget(ProjectDesignApplication application)
    {
        lock (_sync)
        {
            _applications.Remove(application);
        }
    }

    /// <summary>
    /// Closes every application still open — after the participants let go of what they took, before the
    /// generation is asked to go.
    /// </summary>
    private async Task CloseApplicationsAsync()
    {
        ProjectDesignApplication[] open;

        lock (_sync)
        {
            open = [.. _applications];
            _applications.Clear();
        }

        foreach (ProjectDesignApplication application in open)
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Forgets which documents are applications, so the next question reads the projects again.</summary>
    private void ForgetApplicationDocuments()
    {
        lock (_sync)
        {
            _applicationFiles.Clear();
        }
    }

    /// <summary>
    /// Says that a saved file is an application somebody holds — one open, or one a question found — and
    /// forgets which documents are applications.
    /// </summary>
    private async Task ApplicationSavedAsync(CanonicalPath file)
    {
        ProjectIdentity? owner = null;

        lock (_sync)
        {
            foreach (ProjectDesignApplication open in _applications)
            {
                if (open.File == file)
                {
                    owner = open.Project;

                    break;
                }
            }

            if (owner is null)
            {
                foreach ((ProjectIdentity project, CanonicalPath? known) in _applicationFiles)
                {
                    if (known == file)
                    {
                        owner = project;

                        break;
                    }
                }
            }

            _applicationFiles.Clear();
        }

        if (owner is { } changed)
        {
            await RaiseAsync(ApplicationChanged, new ProjectDesignApplicationEventArgs(file, changed)).ConfigureAwait(false);
        }
    }
}

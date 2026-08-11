using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Keeps instances of a project's compiled controls populated from its documents as they are now.
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes: a form that places <c>&lt;views:MyControl /&gt;</c> shows the control the
/// way it was <em>compiled</em>, because the placed instance loads the markup baked into the
/// generation's assembly. Every document is read live — that is the deal ADR 0021 records — except
/// the ones reached through a placed control. This adapter joins Markup's
/// <see cref="XamlLivePopulation"/> to the generation: the host hands over a document, this works
/// out which of the generation's types it is the markup of, and from then on every new instance of
/// that type is populated from the document instead of the build.
/// </para>
/// <para>
/// The pairing is the document's own <c>x:Class</c>, resolved against the generation's rebuildable
/// assemblies — the project's output and its project references, which is where a project's own
/// controls live. A document naming no class, or a class this generation does not have, is
/// answered with <see langword="null"/> rather than a diagnostic: both are ordinary — a
/// class-less resource dictionary, or a control added since the studio opened, which is
/// ADR 0021's restart case and not this type's business.
/// </para>
/// <para>
/// Lifetime follows the generation. The registry holds the generation's types and the documents
/// the host registered, so it must be disposed <em>before</em> the
/// <see cref="ProjectAssemblyContext"/> it was created over — an undisposed registry is a
/// collectible context that never collects.
/// </para>
/// </remarks>
public sealed class ProjectXamlPopulation : IDisposable
{
    private readonly ProjectAssemblyContext _context;
    private readonly XamlLivePopulation _population;
    private readonly Dictionary<string, Type> _classes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private int _disposed;

    private ProjectXamlPopulation(ProjectAssemblyContext context, XamlLivePopulation population)
    {
        _context = context;
        _population = population;
    }

    /// <summary>
    /// Raised when an instance was populated from its compiled markup because the live document
    /// could not do it. See <see cref="XamlLivePopulation.PopulationFailed"/>.
    /// </summary>
    public event EventHandler<XamlLivePopulationFailedEventArgs>? PopulationFailed
    {
        add => _population.PopulationFailed += value;
        remove => _population.PopulationFailed -= value;
    }

    /// <summary>Gets how many of the generation's types currently follow a document.</summary>
    public int Count => _population.Count;

    /// <summary>
    /// Creates a registry over one generation of a project's assemblies.
    /// </summary>
    /// <param name="context">The generation whose types the documents will stand in for.</param>
    /// <param name="environment">
    /// The environment the project's documents load in — the same one
    /// <see cref="ProjectXamlEnvironment.Create"/> built over <paramref name="context"/>, so that
    /// population resolves includes and enters the compilation scope exactly as the sessions do.
    /// </param>
    /// <param name="options">How to populate, or <see langword="null"/> for the defaults.</param>
    /// <returns>The registry. The caller disposes it before disposing the context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="environment"/> is <see langword="null"/>.</exception>
    public static ProjectXamlPopulation Create(
        ProjectAssemblyContext context,
        XamlLoadEnvironment environment,
        XamlLivePopulationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);

        return new ProjectXamlPopulation(context, new XamlLivePopulation(environment, options));
    }

    /// <summary>
    /// Registers a document as what instances of its <c>x:Class</c> type are populated from,
    /// replacing whatever was registered for that type before.
    /// </summary>
    /// <remarks>
    /// The host calls this whenever a document's current text changes hands: on open, after every
    /// applied edit — unsaved edits are exactly the point — and after a reload from disk.
    /// Instances already on screen are not touched; the host rebuilds the previews that place the
    /// control, and their fresh instances land on the registered document.
    /// </remarks>
    /// <param name="document">The document to register.</param>
    /// <param name="cancellationToken">A token to observe while preparing the document.</param>
    /// <returns>
    /// What registering found, or <see langword="null"/> when the document is not the markup of
    /// any of this generation's types — it names no <c>x:Class</c>, or the class is not in the
    /// generation's own assemblies.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This registry has been disposed.</exception>
    public async ValueTask<XamlLivePopulationResult?> SetDocumentAsync(
        XamlDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (ClassOf(document) is not { } className)
        {
            return null;
        }

        Type? type;

        lock (_gate)
        {
            if (!_classes.TryGetValue(className, out type))
            {
                type = FindClass(className);

                if (type is not null)
                {
                    _classes.Add(className, type);
                }
            }
        }

        if (type is null)
        {
            return null;
        }

        return await _population.SetDocumentAsync(type, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a document's registration, so its type's instances populate from the compiled
    /// markup again.
    /// </summary>
    /// <param name="document">The document whose <c>x:Class</c> names the type to release.</param>
    /// <returns><see langword="true"/> when a registration was removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public bool RemoveDocument(XamlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (ClassOf(document) is not { } className)
        {
            return false;
        }

        lock (_gate)
        {
            return _classes.TryGetValue(className, out Type? type) && _population.Remove(type);
        }
    }

    /// <summary>Releases every registration, putting the compiled markup back in charge.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _population.Dispose();

        lock (_gate)
        {
            _classes.Clear();
        }
    }

    private static string? ClassOf(XamlDocument document) =>
        document.Root?.GetDirective(XamlDirectives.Class) is { Length: > 0 } className ? className : null;

    /// <summary>
    /// Finds a class among the generation's rebuildable assemblies.
    /// </summary>
    /// <remarks>
    /// Only the project's output and its project references, which mirrors the type resolver's
    /// search list and the reason for it: a project's own controls are what a build changes and
    /// what documents place by bare name. A package type is not this registry's to override —
    /// nobody is editing its markup here.
    /// </remarks>
    private Type? FindClass(string className)
    {
        foreach (RuntimeAssemblyReference reference in _context.Assemblies)
        {
            if (reference.Origin is not (RuntimeAssemblyOrigin.Project or RuntimeAssemblyOrigin.ProjectReference))
            {
                continue;
            }

            string simpleName = Path.GetFileNameWithoutExtension(reference.Path.FileName);

            if (simpleName.Length == 0 || _context.Resolve(new AssemblyName(simpleName)) is not { } assembly)
            {
                continue;
            }

            if (assembly.GetType(className, throwOnError: false) is { } type)
            {
                return type;
            }
        }

        return null;
    }
}

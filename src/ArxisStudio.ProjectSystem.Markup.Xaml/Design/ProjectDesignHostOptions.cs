using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>How a <see cref="ProjectDesignHost"/> builds, waits and lets go.</summary>
public sealed record ProjectDesignHostOptions
{
    /// <summary>Gets the defaults.</summary>
    public static ProjectDesignHostOptions Default { get; } = new();

    /// <summary>
    /// Gets the evaluated properties the host reads from the snapshot, which a load surfaces only when
    /// asked (<see cref="WorkspaceLoadOptions.AdditionalProperties"/>): whether a project is a test
    /// project, which keeps it out of the design set.
    /// </summary>
    public static ImmutableArray<string> ReadProperties { get; } = ["IsTestProject"];

    /// <summary>
    /// Creates the request a designer loads its workspace with: these options' build properties among
    /// the global ones, so that the snapshot names the outputs the host's builds write, and the
    /// properties the host reads surfaced.
    /// </summary>
    /// <remarks>
    /// A designer that reads more of the evaluation — whether bindings compile by default — adds its
    /// names to the request's <see cref="WorkspaceLoadRequest.Options"/>.
    /// </remarks>
    /// <param name="workspace">The workspace the request is for.</param>
    /// <param name="entryPoint">The solution or project to load.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is <see langword="null"/>.</exception>
    public WorkspaceLoadRequest CreateLoadRequest(ProjectWorkspace workspace, CanonicalPath entryPoint)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new WorkspaceLoadRequest
        {
            Workspace = workspace.Identity,
            EntryPointPath = entryPoint,
            Configuration = Configuration,
            GlobalProperties = BuildProperties,
            Options = WorkspaceLoadOptions.Default with { AdditionalProperties = ReadProperties },
        };
    }

    /// <summary>
    /// Gets the global properties the host's restores and builds run with — for the MSBuild provider,
    /// <c>MSBuildDesignOutput.GlobalProperties</c>, so that the designer's builds never write over the
    /// IDE's. Pass the same to the workspace's loads, so the snapshot names the outputs these builds
    /// write.
    /// </summary>
    public ProjectMetadata BuildProperties { get; init; } = ProjectMetadata.Empty;

    /// <summary>Gets the configuration to build, or <see langword="null"/> for the provider's default.</summary>
    public string? Configuration { get; init; }

    /// <summary>
    /// Gets how long code may stay quiet before the host builds it. Default 400 milliseconds: longer
    /// than an editor's save-all, shorter than a person noticing.
    /// </summary>
    public TimeSpan BuildDelay
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(BuildDelay));

            field = value;
        }
    } = TimeSpan.FromMilliseconds(400);

    /// <summary>Gets the clock the build delay is measured with.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Gets what a document's history calls text taken from its file after somebody else wrote it — a
    /// step the author can undo. Default <c>Changed outside the designer</c>; a designer in another
    /// language says it in that language.
    /// </summary>
    public string ExternalEditDescription
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(ExternalEditDescription));

            field = value;
        }
    } = "Changed outside the designer";

    /// <summary>
    /// Gets what the designer itself keeps of the project's types beyond its participants, let go of
    /// last before a reclaim — the keyboard focus, a third-party library's statics. Called on the user
    /// interface thread, after every participant and document let go.
    /// </summary>
    public Func<CancellationToken, ValueTask>? ReleaseHostState { get; init; }
}

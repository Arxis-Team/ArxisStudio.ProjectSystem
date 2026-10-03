using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Where a <see cref="ProjectDesignHost"/> reads what the project is, and asks for it to be restored and
/// built.
/// </summary>
/// <remarks>
/// <para>
/// A designer of its own loads a <see cref="ProjectWorkspace"/> and hands it over through
/// <see cref="ProjectDesignSource.From(ProjectWorkspace)"/>. A designer inside an IDE does not own the
/// workspace: the IDE's project service evaluates and builds on its own queue, and a second workspace
/// over the same solution would be a second owner of MSBuild racing the first for the restore both share.
/// Such a host implements this over that service instead (ADR 0032).
/// </para>
/// <para>
/// What the host relies on:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Snapshot"/> is evaluated with <see cref="ProjectDesignHostOptions.BuildProperties"/> among
/// its global properties, so that it names the outputs the host's builds write — and with
/// <see cref="ProjectDesignHostOptions.ReadProperties"/> surfaced.
/// </description></item>
/// <item><description>
/// <see cref="SnapshotChanged"/> is raised after <see cref="Snapshot"/> moved, on any thread; the host
/// reads the newest snapshot itself.
/// </description></item>
/// <item><description>
/// <see cref="ExecuteAsync"/> runs the request as given — the host decides when a restore is due — and
/// reports failure as a result, not an exception.
/// </description></item>
/// <item><description>
/// <see cref="RefreshAsync"/> reads the projects again and has published what it read, or kept the
/// previous snapshot, by the time it completes.
/// </description></item>
/// </list>
/// </remarks>
public interface IProjectDesignSource
{
    /// <summary>Gets the newest snapshot, or <see langword="null"/> before the first.</summary>
    SolutionSnapshot? Snapshot { get; }

    /// <summary>Raised after <see cref="Snapshot"/> moved, on any thread.</summary>
    event EventHandler? SnapshotChanged;

    /// <summary>Reads the projects again — after a restore wrote what they reference, or after files changed.</summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task that completes once the reading is published, or given up.</returns>
    ValueTask RefreshAsync(CancellationToken cancellationToken);

    /// <summary>Restores or builds the projects a request names.</summary>
    /// <param name="request">What to run, against which projects, with which properties.</param>
    /// <param name="progress">Where to report progress, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>What the operation did.</returns>
    ValueTask<ProjectOperationResult> ExecuteAsync(
        ProjectOperationRequest request,
        IProgress<ProjectOperationProgress>? progress,
        CancellationToken cancellationToken);
}

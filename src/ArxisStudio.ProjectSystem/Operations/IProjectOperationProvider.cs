using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// An optional second boundary, for a provider that can also change what is on disk.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IProjectSystemProvider"/> rather than part of it, because reading a
/// project and building one are different capabilities and plenty of useful providers have only the
/// first. A provider that invents projects in memory, or reads a format nothing can build, would
/// otherwise have to supply methods that throw — and a contract nobody can honour is worse than one
/// nobody has to.
/// </para>
/// <para>
/// A provider implements this <em>as well as</em> <see cref="IProjectSystemProvider"/>. A workspace
/// discovers it at run time and reports honestly when nothing configured can do what was asked.
/// </para>
/// <para>
/// <b>These operations do not publish a snapshot.</b> A build changes what is on disk, not what a
/// project says, so nothing here advances the workspace version or replaces the current snapshot.
/// A caller that wants the model to reflect a build asks for a refresh afterwards — which is also
/// what keeps builds from being serialised behind loads that have nothing to do with them.
/// </para>
/// <para>
/// <b>Nothing here runs on its own.</b> There is no automatic build after an edit, and no debounce
/// or coalescing: a host that edits rapidly should decide for itself when a build is worth starting,
/// because only the host knows what the user is doing.
/// </para>
/// </remarks>
public interface IProjectOperationProvider
{
    /// <summary>
    /// Determines whether this provider performs a kind of operation.
    /// </summary>
    /// <remarks>
    /// Must be cheap and must not touch the file system, on the same terms as
    /// <see cref="IProjectSystemProvider.CanLoad"/>. Answering <see langword="false"/> is not a
    /// failure — the workspace asks each provider in turn.
    /// </remarks>
    /// <param name="kind">What is being asked for.</param>
    /// <returns><see langword="true"/> when this provider will attempt it.</returns>
    bool CanExecute(ProjectOperationKind kind);

    /// <summary>
    /// Performs the operation.
    /// </summary>
    /// <remarks>
    /// Report problems as diagnostics rather than exceptions, on the same terms as loading: a
    /// compile error is an ordinary outcome and belongs in the result. Cancellation is the one thing
    /// that must be allowed to propagate.
    /// </remarks>
    /// <param name="request">What to do, and in which context.</param>
    /// <param name="progress">Where to report what is happening, or <see langword="null"/> for nowhere.</param>
    /// <param name="cancellationToken">A token to observe. Let cancellation propagate.</param>
    /// <returns>What the operation produced.</returns>
    ValueTask<ProjectOperationResult> ExecuteAsync(
        ProjectOperationRequest request,
        IProgress<ProjectOperationProgress>? progress,
        CancellationToken cancellationToken);
}

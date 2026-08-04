using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// The boundary a format- or engine-specific package implements to populate the core model.
/// </summary>
/// <remarks>
/// <para>
/// Three members, and nothing crossing them that could not be written down: the request and the
/// result are plain data, with no delegate, no <see cref="System.Type"/>, no stream, no
/// <see cref="System.IDisposable"/> and no <see cref="object"/> anywhere in reach. That is what
/// makes a future out-of-process MSBuild worker a change of transport rather than a change of
/// model — the consumer-facing types would not move — and an architecture test checks it rather
/// than trusting this paragraph.
/// </para>
/// <para>
/// A provider never calls back into the workspace. Everything it needs to mint identities travels
/// as values on the request, which is why <see cref="WorkspaceLoadRequest.Workspace"/> is required.
/// </para>
/// <para>
/// <b>Report problems as diagnostics, not exceptions.</b> A missing file, an unparseable project,
/// an unresolved reference — all of those are ordinary and belong in a
/// <see cref="WorkspaceLoadResult"/>. A provider that throws anyway is caught, and the workspace
/// reports <see cref="ProjectDiagnosticCodes.ProviderFailed"/> in its place; the exception does
/// not reach the caller. The one exception to that is cancellation, which must be allowed to
/// propagate.
/// </para>
/// </remarks>
public interface IProjectSystemProvider
{
    /// <summary>
    /// Gets a stable name identifying this provider, used to attribute diagnostics.
    /// </summary>
    /// <remarks>
    /// Stable across versions, and meaningful in a message a user will read: it answers "who said
    /// this" in an error list.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// Determines whether this provider can open an entry point.
    /// </summary>
    /// <remarks>
    /// Must be cheap and must not touch the file system or block: a workspace calls this on every
    /// configured provider, in order, to decide which one to use. Judge from
    /// <see cref="WorkspaceEntryPoint.Kind"/> and <see cref="WorkspaceEntryPoint.Extension"/>.
    /// Answering <see langword="false"/> is not a failure — the next provider is asked, and only if
    /// none accepts does the workspace report
    /// <see cref="ProjectDiagnosticCodes.UnsupportedEntryPoint"/>.
    /// </remarks>
    /// <param name="entryPoint">What the workspace was asked to open.</param>
    /// <returns><see langword="true"/> when this provider will attempt the load.</returns>
    bool CanLoad(WorkspaceEntryPoint entryPoint);

    /// <summary>
    /// Loads an entry point and returns the result.
    /// </summary>
    /// <remarks>
    /// Build the snapshot with <see cref="SolutionSnapshotBuilder"/> and
    /// <see cref="ProjectSnapshotBuilder"/>: they are what turn provider-owned state into
    /// core-owned immutable state, so nothing a provider keeps mutating afterwards can reach a
    /// consumer. Leave <see cref="SolutionSnapshot.Version"/> alone — the workspace stamps it,
    /// because only the workspace knows whether the snapshot was published.
    /// </remarks>
    /// <param name="request">What to open, and how.</param>
    /// <param name="cancellationToken">A token to observe. Let cancellation propagate.</param>
    /// <returns>The result of the load.</returns>
    ValueTask<WorkspaceLoadResult> LoadAsync(WorkspaceLoadRequest request, CancellationToken cancellationToken);
}

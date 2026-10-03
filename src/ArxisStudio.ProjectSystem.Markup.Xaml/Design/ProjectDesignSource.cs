using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>Makes a <see cref="IProjectDesignSource"/> of a workspace the designer owns.</summary>
public static class ProjectDesignSource
{
    /// <summary>A source over a workspace the designer loaded itself.</summary>
    /// <remarks>
    /// Load it with <see cref="ProjectDesignHostOptions.CreateLoadRequest"/>, so that its snapshot names the
    /// outputs the host's builds write. The source holds nothing of its own: a handler of
    /// <see cref="IProjectDesignSource.SnapshotChanged"/> is a handler of the workspace's own event, and
    /// removing it removes it there.
    /// </remarks>
    /// <param name="workspace">The workspace.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is <see langword="null"/>.</exception>
    public static IProjectDesignSource From(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return new WorkspaceSource(workspace);
    }

    private sealed class WorkspaceSource(ProjectWorkspace workspace) : IProjectDesignSource
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<EventHandler, EventHandler<WorkspaceChangedEventArgs>> _forwarded = [];

        public SolutionSnapshot? Snapshot => workspace.CurrentSnapshot;

        public event EventHandler? SnapshotChanged
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                EventHandler<WorkspaceChangedEventArgs> forward = (_, _) => value(this, EventArgs.Empty);

                lock (_sync)
                {
                    if (!_forwarded.TryAdd(value, forward))
                    {
                        return;
                    }
                }

                workspace.SnapshotChanged += forward;
            }

            remove
            {
                if (value is null)
                {
                    return;
                }

                EventHandler<WorkspaceChangedEventArgs>? forward;

                lock (_sync)
                {
                    if (!_forwarded.Remove(value, out forward))
                    {
                        return;
                    }
                }

                workspace.SnapshotChanged -= forward;
            }
        }

        public async ValueTask RefreshAsync(CancellationToken cancellationToken) =>
            await workspace.RefreshAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<ProjectOperationResult> ExecuteAsync(
            ProjectOperationRequest request,
            IProgress<ProjectOperationProgress>? progress,
            CancellationToken cancellationToken) =>
            workspace.ExecuteAsync(request, progress, cancellationToken);
    }
}

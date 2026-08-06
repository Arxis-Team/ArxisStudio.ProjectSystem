using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.MSBuild;
using Avalonia.Threading;

namespace ProjectSystem.Ide.ViewModels;

public sealed partial class IdeViewModel
{
    private ProjectFileWatcher? _watcher;
    private FileChangeCoalescer? _coalescer;

    /// <summary>What the last batch of file changes made stale, in words.</summary>
    public string Staleness
    {
        get;
        private set => Set(ref field, value);
    } = "up to date";

    public bool IsStale
    {
        get;
        private set => Set(ref field, value);
    }

    /// <summary>
    /// Watches everything the open snapshot was built from.
    /// </summary>
    /// <remarks>
    /// The four pieces ADR 0016 describes, composed here because the workspace deliberately does not
    /// compose them: the snapshot says what to watch, the watcher observes it, the coalescer turns a
    /// burst into one batch, and the snapshot decides whether the batch matters. The refresh at the
    /// end is this application's call, with its own token — which is the whole reason the library
    /// leaves it to a caller.
    /// </remarks>
    private void StartWatching()
    {
        StopWatching();

        SolutionSnapshot? snapshot = _workspace.CurrentSnapshot;

        if (snapshot is null)
        {
            return;
        }

        _coalescer = new FileChangeCoalescer(OnChanges, FileChangeCoalescingOptions.Default);
        _watcher = new ProjectFileWatcher(_coalescer.Add);

        // The entry point too: a solution file is not any project's evaluation input, and losing a
        // project from it is exactly the change worth hearing about.
        // Distinct, because it is what is actually watched: the SDK's own targets are an evaluation
        // input of every project in the solution, and summing the lists counted each of them once
        // per project. A number said out loud should be the number of files.
        List<CanonicalPath> watched = [.. snapshot.Projects
            .SelectMany(static project => project.EvaluationInputs)
            .Append(snapshot.EntryPoint.Path)
            .Distinct()];

        _watcher.Watch(watched);

        SetStaleness(WorkspaceInvalidation.None);

        Log($"  watching {Describe(watched.Count, "file")}");
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;

        // Flushed rather than dropped: a batch that arrived while the old snapshot was being
        // replaced still described real edits, and the next invalidation should see them.
        _coalescer?.Flush();
        _coalescer?.Dispose();
        _coalescer = null;
    }

    /// <summary>
    /// Decides what a coalesced batch of changes means. Runs on a timer thread.
    /// </summary>
    private void OnChanges(ImmutableArray<CanonicalPath> batch)
    {
        SolutionSnapshot? snapshot = _workspace.CurrentSnapshot;

        if (snapshot is null)
        {
            return;
        }

        WorkspaceInvalidation invalidation = snapshot.Invalidate(batch);

        Dispatcher.UIThread.Post(() =>
        {
            SetStaleness(invalidation);

            if (!invalidation.IsEmpty)
            {
                // Named rather than counted, because "reloading because Directory.Build.props
                // changed" is a far better thing to read than "reloading".
                foreach (CanonicalPath cause in invalidation.Causes)
                {
                    Log($"  changed: {cause.FileName}");
                }
            }
        });
    }

    private void SetStaleness(WorkspaceInvalidation invalidation)
    {
        IsStale = !invalidation.IsEmpty;

        Staleness = invalidation.Scope switch
        {
            WorkspaceInvalidationScope.EntryPoint => "the solution changed — reload",
            WorkspaceInvalidationScope.Projects =>
                $"{Describe(invalidation.Projects.Length, "project")} stale — refresh",
            _ => "up to date",
        };
    }
}

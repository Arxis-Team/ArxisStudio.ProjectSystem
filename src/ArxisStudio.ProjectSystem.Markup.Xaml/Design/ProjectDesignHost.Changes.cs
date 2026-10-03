using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>Changes from outside — files the owner's watcher saw, snapshots the workspace published — dealt with in order.</summary>
public sealed partial class ProjectDesignHost
{
    /// <summary>
    /// Hands the host a batch of file changes, as the owner's watcher and coalescer delivered them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Batches are dealt with one at a time, in the order they arrive, off the calling thread: a batch
    /// is documents read again, a project re-read, a build scheduled — and the second of two overlapping
    /// ones would act on a snapshot the first is replacing.
    /// </para>
    /// <para>
    /// What a batch means is the snapshot's answer (<see cref="SolutionSnapshot.Classify"/>). A saved
    /// form goes into its document as a step of its history — or, over unsaved edits, into
    /// <see cref="ExternalConflict"/> — and into the placed copies of its control; a form nobody has
    /// open still feeds those. A renamed form follows its file (<see cref="DocumentMoved"/>), a deleted
    /// one is reported (<see cref="DocumentDeleted"/>). The project is re-read when an evaluation input
    /// changed or a file appeared or went away, and saved code is built once the code has been quiet for
    /// <see cref="ProjectDesignHostOptions.BuildDelay"/>.
    /// </para>
    /// </remarks>
    /// <param name="changes">The changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="changes"/> is <see langword="null"/>.</exception>
    public void NotifyChanged(IEnumerable<FileChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        ImmutableArray<FileChange> batch = [.. changes];

        if (!batch.IsEmpty)
        {
            Enqueue(new Work.Changes(batch));
        }
    }

    /// <summary>Queues work for the pump; nothing once the host is disposed.</summary>
    private void Enqueue(Work work)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _work.Writer.TryWrite(work);
        }
    }

    /// <summary>Deals with queued work, one item at a time, until the host is disposed.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        await foreach (Work work in _work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                switch (work)
                {
                    case Work.Changes changes:
                        await SettleAsync(changes.Batch, cancellationToken).ConfigureAwait(false);
                        break;

                    case Work.Published:
                        await SnapshotMovedAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case Work.Edited edited:
                        await DocumentEditedAsync(edited.Document, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                await ReportFailureAsync(work.Operation, exception).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Deals with one batch of file changes.</summary>
    private async Task SettleAsync(ImmutableArray<FileChange> batch, CancellationToken cancellationToken)
    {
        if (_source.Snapshot is not { } snapshot)
        {
            return;
        }

        WorkspaceChangeSet changes = snapshot.Classify(batch);

        if (changes.IsEmpty)
        {
            return;
        }

        // Documents first, because they are what a person is looking at, and they are found by the
        // paths the snapshot the batch arrived against knows.
        foreach (FileRename rename in changes.Renames)
        {
            await FollowAsync(snapshot, rename, cancellationToken).ConfigureAwait(false);
        }

        foreach (FileChange change in batch)
        {
            if (change.Kind == FileChangeKind.Deleted)
            {
                await ReportDeletedAsync(change.Path).ConfigureAwait(false);
            }
        }

        foreach (ProjectItemChange edited in changes.ItemsEdited)
        {
            if (IsMarkupItem(edited.Item) || IsMarkupPath(edited.Item.FullPath))
            {
                await MarkupSavedAsync(snapshot, edited.Item.FullPath, cancellationToken).ConfigureAwait(false);
                await ApplicationSavedAsync(edited.Item.FullPath).ConfigureAwait(false);
            }
        }

        if (changes.RequiresRescan)
        {
            // Changes were lost: every open document is read again — one whose file did not change says
            // so — and every other document registered again.
            DesignDocument[] documents;

            lock (_sync)
            {
                documents = [.. _documents];
            }

            foreach (DesignDocument document in documents)
            {
                await ReloadQuietlyAsync(document, cancellationToken).ConfigureAwait(false);
            }

            await InTurnAsync(() => RegisterDocumentsAsync(snapshot, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        NoteRestoreInputs(snapshot, changes);

        ImmutableArray<ProjectIdentity> code = CodeChanged(snapshot, changes, batch);

        if (changes.RequiresRescan || !changes.Invalidation.IsEmpty || !changes.MembershipChanged.IsEmpty)
        {
            await _source.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!code.IsEmpty)
        {
            ScheduleBuild(code, "the code changed");
        }

        await RaiseAsync(ChangesApplied, new ProjectDesignChangesEventArgs(batch, changes)).ConfigureAwait(false);
    }

    /// <summary>Moves an open document to where its file went, keeping its history and its unsaved edits.</summary>
    private async Task FollowAsync(SolutionSnapshot snapshot, FileRename rename, CancellationToken cancellationToken)
    {
        DesignDocument? moving;

        lock (_sync)
        {
            moving = FindFile(rename.OldPath);
        }

        if (moving is null)
        {
            return;
        }

        await moving.Live.RetargetAsync(UriOf(snapshot, rename.NewPath), cancellationToken).ConfigureAwait(false);

        ProjectIdentity owner = snapshot.TryGetProjectForFile(rename.NewPath, out ProjectSnapshot? project)
            ? project.Identity
            : moving.Project;

        bool reattach;

        lock (_sync)
        {
            moving.File = rename.NewPath;
            reattach = owner != moving.Project && moving.Attached;
            moving.Project = owner;
        }

        if (reattach)
        {
            // Another project's document sees that project's types.
            await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await AttachAsync(moving, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _turn.Release();
            }
        }

        await RaiseAsync(DocumentMoved, new ProjectDesignDocumentEventArgs(moving.Live, rename.NewPath, rename.OldPath)).ConfigureAwait(false);
    }

    /// <summary>Reports the open documents a deletion took the file of — the file, or a folder it was in.</summary>
    private async Task ReportDeletedAsync(CanonicalPath path)
    {
        DesignDocument[] gone;

        lock (_sync)
        {
            gone = FindAtOrBelow(path);
        }

        foreach (DesignDocument document in gone)
        {
            CanonicalPath file;

            lock (_sync)
            {
                file = document.File;
            }

            await RaiseAsync(DocumentDeleted, new ProjectDesignDocumentEventArgs(document.Live, file, file)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes a saved form into its document when it is open, and into the placed copies of its control
    /// either way.
    /// </summary>
    private async Task MarkupSavedAsync(SolutionSnapshot snapshot, CanonicalPath file, CancellationToken cancellationToken)
    {
        DesignDocument? open;

        lock (_sync)
        {
            open = FindFile(file);
        }

        if (open is not null)
        {
            // A document that takes the text says so, and the edit it records is registered from there.
            await ReloadQuietlyAsync(open, cancellationToken).ConfigureAwait(false);

            return;
        }

        await InTurnAsync(
            async () =>
            {
                if (await RegisterFromDiskAsync(snapshot, file, cancellationToken).ConfigureAwait(false) is { } registered)
                {
                    await RefreshDependentsAsync(registered, except: null, cancellationToken).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an open document's file again; a file that cannot be read now is left for its next change.</summary>
    private async Task ReloadQuietlyAsync(DesignDocument document, CancellationToken cancellationToken)
    {
        CanonicalPath file;

        lock (_sync)
        {
            file = document.File;
        }

        SourceText text;

        try
        {
            text = await ReadTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            await TakeExternalTextAsync(document, text, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed while its file was being read.
        }
    }

    /// <summary>Registers an edited document for placed controls and rebuilds what shows them.</summary>
    private async Task DocumentEditedAsync(DesignDocument document, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_documents.Contains(document))
            {
                return;
            }
        }

        await InTurnAsync(
            async () =>
            {
                XamlDocument edited = document.Live.Document;

                if (await RegisterAsync(edited, cancellationToken).ConfigureAwait(false))
                {
                    await RefreshDependentsAsync(edited, except: document, cancellationToken).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs work on the generation's registrations in a turn, so that it lands on one generation and not
    /// between two: a swap registers every document again for its successor, from what it reads then.
    /// </summary>
    private async Task InTurnAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// Notes the projects a restore has to run for before their next build: one of their evaluation
    /// inputs changed, and not one that restore writes itself.
    /// </summary>
    /// <remarks>
    /// Restore's own output changing is a restore having run — the IDE's or the designer's — and
    /// restoring again for it would never stop (<see cref="ProjectSnapshot.RestoreOutputs"/>, ADR 0026).
    /// </remarks>
    private void NoteRestoreInputs(SolutionSnapshot snapshot, WorkspaceChangeSet changes)
    {
        if (changes.Invalidation.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            foreach (ProjectSnapshot project in snapshot.Projects)
            {
                if (changes.Invalidation.Causes.Any(cause => project.EvaluationInputs.Contains(cause) && !project.RestoreOutputs.Contains(cause)))
                {
                    _restorePending.Add(project.Identity);
                }
            }
        }
    }

    /// <summary>
    /// The projects whose code a batch changed: a class saved, one appearing, going away or moving where
    /// a project's globs reach, or the project itself edited.
    /// </summary>
    /// <remarks>
    /// The snapshot has already said which projects such a change belongs to — a class a build writes
    /// into its intermediate folder is nobody's — so a file counts only for a project it named. A project
    /// whose evaluation went stale only because a restore wrote its output is not built for it: the
    /// restore did not change the code, and building after every restore the IDE runs would build for
    /// nothing.
    /// </remarks>
    internal static ImmutableArray<ProjectIdentity> CodeChanged(
        SolutionSnapshot snapshot,
        WorkspaceChangeSet changes,
        ImmutableArray<FileChange> batch)
    {
        var projects = new HashSet<ProjectIdentity>();

        foreach (ProjectItemChange edited in changes.ItemsEdited)
        {
            if (IsCode(edited.Item))
            {
                projects.Add(edited.Project);
            }
        }

        foreach (FileChange change in batch)
        {
            if (change.Kind is FileChangeKind.Changed or FileChangeKind.Overflow)
            {
                continue;
            }

            // What arrived: a class file where a project's globs reach.
            if (change.Kind is FileChangeKind.Created or FileChangeKind.Renamed
                && IsCodePath(change.Path)
                && snapshot.TryGetProjectForFile(change.Path, out ProjectSnapshot? arrivedIn)
                && (changes.MembershipChanged.Contains(arrivedIn.Identity) || changes.Invalidation.Projects.Contains(arrivedIn.Identity)))
            {
                projects.Add(arrivedIn.Identity);
            }

            // What left: a class the snapshot declares, or a folder some were in.
            CanonicalPath left = change.Kind == FileChangeKind.Renamed ? change.OldPath : change.Kind == FileChangeKind.Deleted ? change.Path : default;

            if (!left.IsEmpty)
            {
                foreach (ProjectSnapshot project in snapshot.Projects)
                {
                    if (project.Items.Any(item => IsCode(item) && item.FullPath.StartsWith(left)))
                    {
                        projects.Add(project.Identity);
                    }
                }
            }
        }

        if (changes.Invalidation.Scope == WorkspaceInvalidationScope.Projects)
        {
            foreach (ProjectIdentity identity in changes.Invalidation.Projects)
            {
                if (snapshot.TryGetProject(identity, out ProjectSnapshot? project)
                    && changes.Invalidation.Causes.Any(cause => project.EvaluationInputs.Contains(cause) && !project.RestoreOutputs.Contains(cause)))
                {
                    projects.Add(identity);
                }
            }
        }

        return [.. snapshot.Projects.Select(static project => project.Identity).Where(projects.Contains)];
    }

    private static bool IsCode(ProjectItem item) =>
        string.Equals(item.ItemType, ProjectItemTypes.Compile, StringComparison.OrdinalIgnoreCase);

    private static bool IsCodePath(CanonicalPath path) =>
        path.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Queues a published snapshot for the pump, and answers resources from it at once.</summary>
    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        // The newest snapshot, read rather than carried: notifications may arrive out of order.
        if (_source.Snapshot is not { } snapshot)
        {
            return;
        }

        lock (_sync)
        {
            if (_resources is not null)
            {
                _resources = ProjectResourceMap.Create(snapshot);
            }
        }

        ForgetApplicationDocuments();

        Enqueue(new Work.Published());
    }

    /// <summary>
    /// Decides what a newly published snapshot means for the live generation: nothing, a swap — the
    /// projects shown or what they reference changed — or a restart, when a package the generation
    /// loaded moved to another file.
    /// </summary>
    private async Task SnapshotMovedAsync(CancellationToken cancellationToken)
    {
        bool stale = false;

        await InTurnAsync(
            async () =>
            {
                stale = await GenerationOutlivedAsync().ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (stale)
        {
            await ReportStateAsync().ConfigureAwait(false);

            TrySwapSoon();
        }
    }

    /// <summary>
    /// Whether the newest snapshot leaves the live generation behind — and a restart when it cannot be
    /// left behind in this process. In a turn, so that a swap in flight is over and its successor is what
    /// is compared.
    /// </summary>
    private async Task<bool> GenerationOutlivedAsync()
    {
        if (_source.Snapshot is not { } snapshot)
        {
            return false;
        }

        ProjectAssemblyContext? generation;
        bool started;

        lock (_sync)
        {
            generation = _generation;
            started = _started && _restartReason is null;
        }

        if (!started)
        {
            return false;
        }

        ImmutableArray<ProjectIdentity> set = DesignSetOf(snapshot);

        if (generation is not null && !generation.IsUnloaded && PackageMoved(generation, snapshot, set) is { } package)
        {
            await RequireRestartAsync(
                ProjectDesignRestartReason.PackagesChanged,
                $"The package assembly '{package}' moved to another file, and the one this process loaded stays loaded for as long as "
                    + "it runs. Only a new process shows the project with the package as it is now.").ConfigureAwait(false);

            return false;
        }

        if (generation is null ? set.IsEmpty : !generation.IsUnloaded && IsGenerationOf(generation, snapshot, set))
        {
            return false;
        }

        MarkStale("the projects shown, or what they reference, changed");

        return true;
    }

    /// <summary>Whether a generation is of exactly the projects and assemblies a snapshot's design set has.</summary>
    private static bool IsGenerationOf(ProjectAssemblyContext generation, SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> set)
    {
        if (!generation.Projects.SequenceEqual(set))
        {
            return false;
        }

        var paths = new HashSet<CanonicalPath>();

        foreach (ProjectIdentity project in set)
        {
            foreach (RuntimeAssemblyReference assembly in snapshot.GetRuntimeAssemblies(project))
            {
                paths.Add(assembly.Path);
            }
        }

        return paths.SetEquals(generation.Assemblies.Select(static assembly => assembly.Path));
    }

    /// <summary>A package the generation loaded that the snapshot now has in another file, by name.</summary>
    private static string? PackageMoved(ProjectAssemblyContext generation, SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> set)
    {
        IReadOnlyDictionary<string, CanonicalPath> loaded = generation.PackagesLoaded;

        if (loaded.Count == 0)
        {
            return null;
        }

        foreach (ProjectIdentity project in set)
        {
            foreach (RuntimeAssemblyReference assembly in snapshot.GetRuntimeAssemblies(project))
            {
                if (assembly.Origin == RuntimeAssemblyOrigin.Package
                    && loaded.TryGetValue(Path.GetFileNameWithoutExtension(assembly.Path.Value), out CanonicalPath file)
                    && file != assembly.Path)
                {
                    return Path.GetFileNameWithoutExtension(assembly.Path.Value);
                }
            }
        }

        return null;
    }

    /// <summary>Records that the generation is to be replaced, and why.</summary>
    private void MarkStale(string reason)
    {
        lock (_sync)
        {
            _stale = true;
            _staleReason = reason;
        }
    }

    /// <summary>Work for the pump.</summary>
    private abstract record Work(string Operation)
    {
        /// <summary>A batch of file changes.</summary>
        public sealed record Changes(ImmutableArray<FileChange> Batch) : Work("dealing with changed files");

        /// <summary>The workspace published a snapshot.</summary>
        public sealed record Published() : Work("following the project");

        /// <summary>A document's text changed, by the designer, undo, or text taken from its file.</summary>
        public sealed record Edited(DesignDocument Document) : Work("following an edited document");
    }
}

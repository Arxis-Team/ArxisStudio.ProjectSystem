using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;

namespace ArxisStudio.ProjectSystem;

// What file changes mean for this snapshot, and the lookups that answer it.
public sealed partial class SolutionSnapshot
{
    // Built on first use and then kept: a snapshot is read from many threads without a lock, and
    // most snapshots are never asked. The race to build it is harmless — both builders produce the
    // same index and the first stored wins.
    private ChangeIndex? _changeIndex;

    /// <summary>
    /// Finds the item a file is, and the project that declares it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An index built once per snapshot, on first use: a host that classifies every save of every file
    /// asks this for each, and a walk over every item of every project per question would grow with
    /// the solution.
    /// </para>
    /// <para>
    /// When two projects declare the same file — which linked files make possible — the first in
    /// snapshot order is the answer, as it is for <see cref="TryGetProjectForFile"/>.
    /// </para>
    /// </remarks>
    /// <param name="path">The file.</param>
    /// <param name="project">The project that declares it, when one does.</param>
    /// <param name="item">The item, when a project declares it.</param>
    /// <returns><see langword="true"/> when a project declares the file.</returns>
    public bool TryGetItem(
        CanonicalPath path,
        [NotNullWhen(true)] out ProjectSnapshot? project,
        [NotNullWhen(true)] out ProjectItem? item)
    {
        if (!path.IsEmpty && Index().Items.TryGetValue(path, out ImmutableArray<(ProjectSnapshot Project, ProjectItem Item)> declaring))
        {
            (project, item) = declaring[0];

            return true;
        }

        project = null;
        item = null;

        return false;
    }

    /// <summary>
    /// Works out what a batch of file changes means for this workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Invalidate"/> answers "which evaluations are stale", and that is all a host that only
    /// re-evaluates needs. A host that shows files needs more, and the kind of each change is what
    /// tells it (<see href="../../docs/adr/0025-file-changes-carry-their-kind-and-the-snapshot-classifies-them.md">ADR 0025</see>).
    /// Every change is asked each question below on its own, because one file can be several things at
    /// once: a <c>Directory.Build.props</c> beside a project is an import its evaluation read and an
    /// item its globs took.
    /// </para>
    /// <list type="bullet">
    /// <item><b>Was a change lost?</b> <see cref="FileChangeKind.Overflow"/> means everything has to be
    /// looked at again: <see cref="WorkspaceChangeSet.RequiresRescan"/>.</item>
    /// <item><b>Is an evaluation stale?</b> When an evaluation input — the solution, a project file, an
    /// import, restore output — changed, appeared, went away or was renamed, exactly as
    /// <see cref="Invalidate"/> says: <see cref="WorkspaceChangeSet.Invalidation"/>. An import that
    /// was not there yet is an input too.</item>
    /// <item><b>Is a document to be read again?</b> When the contents of a file a project declares
    /// changed: <see cref="WorkspaceChangeSet.ItemsEdited"/>. That alone makes no evaluation
    /// stale.</item>
    /// <item><b>May a project include different files?</b> When a file appeared, went away or was
    /// renamed where a project's globs reach — inside its directory, outside its build directories
    /// (<see cref="ProjectSnapshot.BuildDirectories"/>), which are a build working, and outside
    /// directories whose names start with a dot, where tools keep their state and which SDK projects
    /// exclude from every glob — or a file a project declares did, wherever it is:
    /// <see cref="WorkspaceChangeSet.MembershipChanged"/>. Only an evaluation can say what the globs
    /// take now.</item>
    /// <item><b>Did a declared file move?</b> <see cref="WorkspaceChangeSet.Renames"/>, so that
    /// whatever had it open follows it.</item>
    /// </list>
    /// <para>
    /// <b>A path may be a directory.</b> A watcher reports a directory renamed, deleted or moved in as
    /// one change, however much it held, so a path that appears, goes away or is renamed stands for
    /// everything the snapshot knows below it: the inputs there are stale, the files declared there
    /// are their projects' to lose, and a directory renamed moves every declared file in it. A file
    /// changing and a directory changing are told apart by the snapshot alone — a directory is never
    /// an item or an input, so its <see cref="FileChangeKind.Changed"/> is nothing.
    /// </para>
    /// <para>
    /// Pure, like <see cref="Invalidate"/>: the file system is not asked anything, which is why the
    /// kind has to come with the change. Feed it what <see cref="FileChangeCoalescer.ForChanges"/>
    /// delivers, so that an atomic save arrives as the one change it is. A rename missing one of its
    /// paths counts as the end it has — an arrival or a departure.
    /// </para>
    /// </remarks>
    /// <param name="changes">The changes, in the order they happened.</param>
    /// <returns>What they mean.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="changes"/> is <see langword="null"/>.</exception>
    public WorkspaceChangeSet Classify(IEnumerable<FileChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        ChangeIndex index = Index();
        var inputs = new List<CanonicalPath>();
        ImmutableArray<ProjectItemChange>.Builder edited = ImmutableArray.CreateBuilder<ProjectItemChange>();
        var editedPaths = new HashSet<CanonicalPath>();
        var membership = new HashSet<ProjectIdentity>();
        ImmutableArray<FileRename>.Builder renames = ImmutableArray.CreateBuilder<FileRename>();
        var renamed = new HashSet<CanonicalPath>();
        bool rescan = false;

        foreach (FileChange change in changes)
        {
            switch (change.Kind)
            {
                case FileChangeKind.Overflow:
                    rescan = true;
                    break;

                case FileChangeKind.Changed:
                    Edited(change.Path);
                    break;

                case FileChangeKind.Created:
                case FileChangeKind.Deleted:
                    Structural(change.Path);
                    break;

                case FileChangeKind.Renamed:
                    Structural(change.OldPath);
                    Structural(change.Path);
                    Moved(change.OldPath, change.Path);
                    break;
            }
        }

        WorkspaceInvalidation invalidation = Invalidate(inputs);

        ImmutableArray<ProjectIdentity> moved = invalidation.Scope == WorkspaceInvalidationScope.EntryPoint
            ? []
            : [.. Projects
                .Select(static project => project.Identity)
                .Where(identity => membership.Contains(identity) && !invalidation.Projects.Contains(identity))];

        // A file renamed there and back again went nowhere.
        ImmutableArray<FileRename> went = [.. renames.Where(static rename => rename.OldPath != rename.NewPath)];

        bool nothing = invalidation.IsEmpty && edited.Count == 0 && moved.IsEmpty && went.IsEmpty && !rescan;

        return nothing
            ? WorkspaceChangeSet.None
            : new WorkspaceChangeSet(invalidation, edited.ToImmutable(), moved, went, rescan);

        // A file's contents changed: an evaluation that read it is stale, a document that is it is to
        // be read again.
        void Edited(CanonicalPath path)
        {
            if (path.IsEmpty)
            {
                return;
            }

            if (path == EntryPoint.Path || index.Inputs.Contains(path))
            {
                inputs.Add(path);
            }

            if (index.Items.TryGetValue(path, out ImmutableArray<(ProjectSnapshot Project, ProjectItem Item)> declaring)
                && editedPaths.Add(path))
            {
                edited.Add(new ProjectItemChange(declaring[0].Project.Identity, declaring[0].Item));
            }
        }

        // A path appeared, went away or was renamed — and with it, if it is a directory, everything
        // the snapshot knows below it.
        void Structural(CanonicalPath path)
        {
            if (path.IsEmpty)
            {
                return;
            }

            if (EntryPoint.Path.StartsWith(path))
            {
                inputs.Add(EntryPoint.Path);
            }

            foreach (CanonicalPath input in AtOrBelow(index.InputPaths, path))
            {
                inputs.Add(input);
            }

            // A declared file outside every project's directory — a linked one — is still its
            // projects' to lose: every one of them, not only the first.
            foreach (CanonicalPath declared in AtOrBelow(index.ItemPaths, path))
            {
                foreach ((ProjectSnapshot project, _) in index.Items[declared])
                {
                    membership.Add(project.Identity);
                }
            }

            foreach (ProjectSnapshot project in Projects)
            {
                if (Reaches(project, path))
                {
                    membership.Add(project.Identity);
                }
            }
        }

        // Every declared file at or below the old path is now at the same place below the new one —
        // including one an earlier rename of the batch already took there: a file renamed inside a
        // directory renamed in the same batch is one move, from where the snapshot has it to where it
        // ended, whichever of the two came first.
        void Moved(CanonicalPath from, CanonicalPath to)
        {
            if (from.IsEmpty || to.IsEmpty)
            {
                return;
            }

            for (int i = 0; i < renames.Count; i++)
            {
                if (renames[i].NewPath.StartsWith(from))
                {
                    renames[i] = renames[i] with { NewPath = Rebase(renames[i].NewPath, from, to) };
                }
            }

            foreach (CanonicalPath declared in AtOrBelow(index.ItemPaths, from))
            {
                if (renamed.Add(declared))
                {
                    renames.Add(new FileRename(declared, Rebase(declared, from, to)));
                }
            }
        }
    }

    /// <summary>
    /// Whether a path is where a project's globs can reach: inside its directory, outside its build
    /// directories, and not under a directory whose name starts with a dot below it.
    /// </summary>
    /// <remarks>
    /// Only the part below the project's own directory is looked at for dots. A project can live
    /// anywhere — under a user's profile, in a temporary folder whose path holds a dot-directory — and
    /// a rule over the whole path would make every change to it noise.
    /// </remarks>
    private static bool Reaches(ProjectSnapshot project, CanonicalPath path)
    {
        // The project's directory itself reaches it too, and says nothing new: what happens to it
        // happens to the project file below, which is always an input.
        if (!path.StartsWith(project.ProjectDirectory))
        {
            return false;
        }

        foreach (CanonicalPath directory in project.BuildDirectories)
        {
            if (path.StartsWith(directory))
            {
                return false;
            }
        }

        string below = path.Value[project.ProjectDirectory.Value.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string[] segments = below.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // Every segment but the last names a directory the path is in.
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].StartsWith('.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The paths at or below a path, in an array sorted by <see cref="CanonicalPath.CompareTo"/>.
    /// </summary>
    /// <remarks>
    /// Paths that begin with the same text sort together, so the candidates are one contiguous run
    /// found by a binary search; the run is then checked segment by segment, because
    /// <c>C:\src\App2</c> begins with <c>C:\src\App</c> and is not below it.
    /// </remarks>
    private static IEnumerable<CanonicalPath> AtOrBelow(ImmutableArray<CanonicalPath> sorted, CanonicalPath path)
    {
        int index = sorted.BinarySearch(path);

        for (index = index < 0 ? ~index : index; index < sorted.Length; index++)
        {
            CanonicalPath candidate = sorted[index];

            if (!candidate.Value.StartsWith(path.Value, CanonicalPathFormat.Comparison))
            {
                yield break;
            }

            if (candidate.StartsWith(path))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>Where a path at or below <paramref name="from"/> is once that moved to <paramref name="to"/>.</summary>
    private static CanonicalPath Rebase(CanonicalPath path, CanonicalPath from, CanonicalPath to)
    {
        string rest = path.Value[from.Value.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return rest.Length == 0 ? to : to.Combine(rest);
    }

    private ChangeIndex Index()
    {
        if (Volatile.Read(ref _changeIndex) is { } built)
        {
            return built;
        }

        var items = new Dictionary<CanonicalPath, ImmutableArray<(ProjectSnapshot, ProjectItem)>.Builder>();
        var inputs = new HashSet<CanonicalPath>();

        foreach (ProjectSnapshot project in Projects)
        {
            inputs.UnionWith(project.EvaluationInputs.Where(static input => !input.IsEmpty));

            foreach (ProjectItem item in project.Items)
            {
                if (item.FullPath.IsEmpty)
                {
                    continue;
                }

                if (!items.TryGetValue(item.FullPath, out ImmutableArray<(ProjectSnapshot, ProjectItem)>.Builder? declaring))
                {
                    declaring = ImmutableArray.CreateBuilder<(ProjectSnapshot, ProjectItem)>(1);
                    items.Add(item.FullPath, declaring);
                }

                declaring.Add((project, item));
            }
        }

        var index = new ChangeIndex(
            items.ToFrozenDictionary(static pair => pair.Key, static pair => pair.Value.ToImmutable()),
            [.. items.Keys.Order()],
            inputs.ToFrozenSet(),
            [.. inputs.Order()]);

        return Interlocked.CompareExchange(ref _changeIndex, index, null) ?? index;
    }

    /// <summary>The lookups <see cref="Classify"/> and <see cref="TryGetItem"/> answer from.</summary>
    /// <param name="Items">
    /// The items that are a file, in snapshot order — the first is <see cref="TryGetItem"/>'s answer.
    /// Usually one: a file linked into several projects is the case the array exists for.
    /// </param>
    /// <param name="ItemPaths">The declared files, sorted, so that a directory's are one run.</param>
    /// <param name="Inputs">What any project's evaluation read.</param>
    /// <param name="InputPaths">The same, sorted.</param>
    private sealed record ChangeIndex(
        FrozenDictionary<CanonicalPath, ImmutableArray<(ProjectSnapshot Project, ProjectItem Item)>> Items,
        ImmutableArray<CanonicalPath> ItemPaths,
        FrozenSet<CanonicalPath> Inputs,
        ImmutableArray<CanonicalPath> InputPaths);
}

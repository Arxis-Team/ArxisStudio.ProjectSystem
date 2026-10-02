# 0025. File changes carry their kind, and the snapshot classifies them

Date: 2026-10-03

Status: Accepted. Amends [0016](0016-watching-belongs-with-the-provider.md).

## Context

[ADR 0016](0016-watching-belongs-with-the-provider.md) composed watching from four pieces and gave
the decision to `SolutionSnapshot.Invalidate`, over bare paths. That answers one question — which
evaluations are stale — and while the only consumer was a host that re-evaluates, it was the only
question.

The form designer is a host that shows files, beside an IDE that edits them
(`UiDesigner.Demo` in ArxisStudio.Surface, [ADR 0024](0024-the-designer-sample-lives-with-the-designer.md)).
For every batch it has four more questions: which open documents to read again, which projects may
include different files now, which documents moved, and whether changes were lost. Bare paths cannot
answer any of them. A path that changed and a path that appeared look the same, and so do a file and
a directory. The designer answered by treating everything it could not explain as structural and
evaluating MSBuild again — on every save, because of how editors save.

**How editors save.** JetBrains Rider's safe write, like every atomic writer, puts the text in a
temporary file (`X___jb_tmp___`), renames the original aside (`X___jb_old___`), renames the temporary
over it and deletes the original: five events, four of them structural, for one edit of one file.
Other editors differ in the names and the order, not in the shape.

**What a watcher sees is directories.** A folder renamed, deleted or moved in is one event; its
contents are not reported. A folder renamed in the IDE moves every form in it, and a watcher says
nothing about any of them.

**A build writes where it is watched.** `bin` and `obj` sit inside the project directory, and a
build — the designer's own included — writes thousands of files there that are not the project
changing.

## Decision

**A change carries its kind.** `FileChange` is a path and a `FileChangeKind` — `Changed`, `Created`,
`Deleted`, `Renamed` (with `OldPath`) and `Overflow` for changes that were lost. A core type: it
names what a watcher saw and touches nothing.

**The coalescer nets each path over the batch.** `FileChangeCoalescer.ForChanges` delivers what is
true at the end of the batch, because only the first and last state of a path matter to anybody
reading it:

| First | Then | Is |
| --- | --- | --- |
| created | deleted | nothing |
| created | anything else | created |
| deleted | deleted | deleted |
| deleted | anything else | changed — the file was replaced |
| there before | deleted | deleted |
| there before | anything else | changed |

A rename is two of these — a departure from the old path, an arrival at the new — and stays a rename
only where its ends are still a departure and an arrival at the end of the batch; renames in a chain
are one rename from the first path to the last. A lost change comes first. Rider's save is then one
`Changed` of the saved file, by arithmetic rather than by a list of editors' temporary names. A rename
missing one of its paths is the end it has. The path constructor is fed the same netting, so a host
on paths stops seeing temporary files too; it hears both ends of a rename and nothing of an overflow,
which a path cannot say — `ProjectFileWatcher` reports every watched path itself when its buffer
overflows.

**The snapshot classifies.** `SolutionSnapshot.Classify` asks each change five questions, each on its
own, because one file can be several things — a `Directory.Build.props` beside a project is an import
its evaluation read and a `None` item its globs took:

- **Were changes lost?** `RequiresRescan`.
- **Is an evaluation stale?** An input changed, appeared, went away or was renamed: `Invalidation`,
  which is `Invalidate`'s answer for those inputs.
- **Is a document to be read again?** A declared file's contents changed: `ItemsEdited`. That alone
  makes nothing stale.
- **May a project include different files?** A path appeared, went away or was renamed where the
  project's globs reach — inside its directory, outside its build directories, outside directories
  below it whose names start with a dot — or a file it declares did, wherever that is:
  `MembershipChanged`. Whether a glob takes the file is the evaluation's question, so the answer is
  "evaluate this project", never a guessed item.
- **Did a declared file move?** `Renames`, so that whatever had it open follows it.

A path may be a directory, and the snapshot alone tells: a path that appears, goes away or is renamed
stands for everything the snapshot knows at or below it — its inputs are stale, its declared files
are their projects' to lose, and a directory renamed moves every declared file in it. Renames compose
across the batch, so a file renamed inside a directory renamed in the same batch is one move,
whichever came first. A directory is never an item or an input, so its `Changed` is nothing. The
lookups are an index built once per snapshot on first use — declared files and inputs, each sorted so
that a directory's are one run for a binary search.

**The snapshot says where builds write.** `ProjectSnapshot.BuildDirectories` holds the roots a build
writes under. The MSBuild provider names `BaseOutputPath` and `BaseIntermediateOutputPath` as the
project evaluated them — `bin` and `obj` unless the project moved them — because another
configuration's build and another tool's build write below them too; this configuration's own
`OutputPath` and `IntermediateOutputPath` are named only where they lie outside both. Empty means no
file is a build file, which a classifier answers by evaluating more, not less.

## Consequences

- **A save in the IDE re-evaluates nothing.** It is one `ItemsEdited` entry; a new file is one
  project evaluated again, not the solution; a build writing its output is `WorkspaceChangeSet.None`.
- **`Invalidate` stays, unchanged.** A host that only re-evaluates keeps using it on paths. It
  compares paths exactly, so a directory event says nothing to it; a host fed directory events — any
  host on `FileSystemWatcher` with `DirectoryName` — needs `Classify`.
- **The kind comes from the watcher.** A watcher that reports bare paths still works, through
  `Add(CanonicalPath)`, as `Changed` — which is what it always meant. Classified, such a batch can say
  what is stale and which documents changed, never what appeared or moved.
- **Membership over-approximates, by design.** A file appearing in a project's directory makes that
  project worth evaluating whether or not a glob takes it; the alternative is reimplementing MSBuild's
  globs, excludes and `Remove` items in the core, and being wrong about them.
- **Linked files have two rules.** An edited one is reported once, as the first declaring project's
  item — a document is read again once. A deleted one changes the membership of every project that
  declares it.
- **Dots count below the project, not above it.** A project can live under a directory whose name
  starts with a dot — a tool folder, a profile path — and its files are still its files.

## Rejected alternatives

- **Netting by name.** Lists of editors' temporary-file patterns (`___jb_tmp___`, `~`, `.tmp`, `.swp`)
  rot with every editor release and still miss the rename over the original. Netting by first and
  last state needs no list.
- **Classifying in the MSBuild package beside the watcher.** It is pure logic over a snapshot, like
  `Invalidate`, and belongs where `Invalidate` is — in the core, testable without MSBuild.
- **Asking the file system whether a path is a directory.** The core has no file system access
  (`CoreSourceRulesTests`), and a deleted path has no answer to give anyway. The snapshot knows what
  was below a path, which is what the question was for.
- **Netting in `Classify`.** The coalescer has the burst in the order it happened; `Classify` takes a
  batch that is already what it amounts to. Netting twice, in two places, would be two answers.

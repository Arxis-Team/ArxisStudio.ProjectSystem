# ArxisStudio.ProjectSystem — API guide

A model of a .NET solution that is safe to read from any thread while something else reloads it, and
that does not drag a build engine into your process.

Every example uses only public API.

## Two libraries, and which one you need

`ArxisStudio.ProjectSystem` is the model: identities, snapshots, diagnostics, the provider boundary
and the workspace. It **reads nothing** — no `.sln`, `.slnx` or `.csproj` parsing, no MSBuild — and a
tool that only wants to hold, cache or render a solution model references it alone.

`ArxisStudio.ProjectSystem.MSBuild` is what fills the model in, by evaluating real projects, and what
runs restore and build over them. Reference it when you want to open or build something on disk.

Implementing `IProjectSystemProvider` yourself remains useful after that: it is how a tool built on
this model gets tested without an SDK on the machine, and the example below is exactly that.

## The shape of it

```text
ProjectWorkspace          owns the current snapshot, its version, and the order of changes
    └── SolutionSnapshot  one immutable picture of everything open, at one version
            └── ProjectSnapshot   one project: references, items, outputs, diagnostics
```

`IProjectSystemProvider` is what fills that in. The workspace calls it, checks what comes back, and
publishes. `IProjectOperationProvider` is the separate, optional capability for changing what is on
disk — restore, build, rebuild, clean — and it publishes nothing.

## Ten minutes end to end

A provider that invents one project, and a workspace that publishes it.

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;

sealed class InMemoryProvider : IProjectSystemProvider
{
    public string Name => "InMemory";

    // Cheap, no file system, no blocking: the workspace asks every provider in turn.
    public bool CanLoad(WorkspaceEntryPoint entryPoint) =>
        entryPoint.Kind == WorkspaceEntryPointKind.Project;

    public ValueTask<WorkspaceLoadResult> LoadAsync(
        WorkspaceLoadRequest request, CancellationToken cancellationToken)
    {
        // Identity is derived from (workspace, canonical path), so reloading the same file in the
        // same workspace always produces the same identity. Nothing has to remember it.
        var identity = ProjectIdentity.Create(request.Workspace, request.EntryPointPath);

        var project = new ProjectSnapshotBuilder
        {
            Identity = identity,
            Name = request.EntryPointPath.FileName,
            ProjectFilePath = request.EntryPointPath,
            Language = "C#",
            ProviderName = Name,
        };

        project.TargetFrameworks.Add("net10.0");
        project.Properties["OutputType"] = "Library";

        project.Items.Add(new ProjectItem
        {
            ItemType = ProjectItemTypes.Compile,
            Include = "Program.cs",
            FullPath = request.EntryPointPath.Directory.Combine("Program.cs"),
            Origin = ProjectItemOrigin.Declared,
        });

        project.PackageReferences.Add(new PackageReferenceInfo
        {
            PackageId = "Serilog",
            VersionText = "4.1.0",   // text, uninterpreted: the core does not parse version ranges
        });

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = request.Workspace,
            Name = request.EntryPointPath.FileName,
            ProviderName = Name,
            Request = request,
        };

        solution.Projects.Add(project.ToSnapshot());

        return ValueTask.FromResult(WorkspaceLoadResult.Success(solution.ToSnapshot()));
    }
}
```

And the consuming side:

```csharp
await using var workspace = new ProjectWorkspace(new InMemoryProvider());

workspace.SnapshotChanged += (_, e) =>
    Console.WriteLine($"version {e.Version}: {e.Snapshot.Projects.Length} project(s)");

WorkspaceLoadResult result = await workspace.LoadAsync(new WorkspaceLoadRequest
{
    Workspace = workspace.Identity,
    EntryPointPath = CanonicalPath.Create(@"C:\src\App\App.csproj"),
});

if (result.HasSnapshot)
{
    foreach (ProjectSnapshot project in result.Snapshot!.Projects)
    {
        Console.WriteLine($"{project.Name} -> {string.Join(", ", project.TargetFrameworks)}");
    }
}

foreach (ProjectDiagnostic diagnostic in result.Diagnostics)
{
    Console.WriteLine(diagnostic);   // "Error APS1002: ... (C:\src\App\App.csproj)"
}
```

Nothing was evaluated, and no build engine was loaded.

## A property the provider does not surface

`ProjectSnapshot.Properties` is curated: a full MSBuild evaluation holds about a thousand properties,
and the MSBuild provider copies the few dozen a tool commonly needs. A consumer whose question is a
property nobody else asks names it on the request and gets the answer from the evaluation the
workspace runs anyway — not from a second evaluation, and not from reading the project file by hand:

```csharp
WorkspaceLoadResult result = await workspace.LoadAsync(new WorkspaceLoadRequest
{
    Workspace = workspace.Identity,
    EntryPointPath = solutionPath,
    Options = new WorkspaceLoadOptions { AdditionalProperties = ["AvaloniaUseCompiledBindingsByDefault"] },
});

bool compiled = string.Equals(
    project.Properties.GetValueOrDefault("AvaloniaUseCompiledBindingsByDefault"), "true",
    StringComparison.OrdinalIgnoreCase);
```

Names compare case-insensitively, as MSBuild's do, and a property the project does not set is
absent. Two requests that name the same properties are equal, however their arrays were built, so a
host can still ask whether a load is the one it already ran.

## Paths

Every path in the model is a `CanonicalPath`: absolute, normalised, and compared by one documented
policy. A `string` in the model is a display name — `ProjectItem.Include` may be relative, a glob, or
not a path at all, which is exactly why it is not a `CanonicalPath`.

```csharp
CanonicalPath project = CanonicalPath.Create(@"C:\src\App\App.csproj");
CanonicalPath sibling = CanonicalPath.Create(project.Directory, @"..\Core\Core.csproj");

project.StartsWith(CanonicalPath.Create(@"C:\src"));   // true, and segment-aware
```

**Comparison is case-insensitive on every operating system**, because MSBuild's is, and a model that
disagreed would put one project into the graph twice. See
[ADR 0005](../adr/0005-one-path-policy-and-it-is-case-insensitive.md).

Nothing here touches the file system. A path naming a file that does not exist is a perfectly valid
`CanonicalPath`; noticing it is missing is a provider's job.

## Identity and staleness

`ProjectIdentity` is derived from `(workspace, canonical project path)`, so it is deterministic
within one workspace and needs no registry. It is **not stable across process launches** and must not
be persisted — store a `CanonicalPath` instead.

To decide whether cached state is current, compare the version:

```csharp
if (cachedVersion != workspace.CurrentSnapshot?.Version)
{
    // rebuild whatever was derived
}
```

Read `snapshot.Version` rather than `workspace.CurrentVersion` when you also hold the snapshot:
that pair comes from one publication and cannot disagree, whereas two reads of a live workspace can
straddle one.

## Which assembly holds a type

The question a designer or an analyzer actually needs answered. Three places hold the pieces, and a
snapshot has all three:

```csharp
ProjectSnapshot project = snapshot.Projects[0];

// Its own build output, for the framework this snapshot was evaluated under.
CanonicalPath? own = project.Outputs
    .FirstOrDefault(o => o.Kind == OutputArtifactKind.Assembly)?.Path;

// A referenced project's output: follow the identity, then read that project's outputs.
foreach (ProjectReferenceInfo reference in project.ProjectReferences)
{
    if (snapshot.TryGetProject(reference.Project, out ProjectSnapshot? target))
    {
        // target.Outputs holds its assemblies
    }
}

// Everything restore resolved, including packages nothing declared directly.
foreach (ResolvedPackage package in project.ResolvedPackages)
{
    // package.RuntimeAssemblies to load; package.CompileAssemblies to compile against
}
```

`CompileAssemblies` and `RuntimeAssemblies` are separate because a package can be compiled against a
reference assembly and contribute nothing at run time — `ExcludeAssets="runtime"` produces exactly
that. Use the one that matches what you are building.

**A project's own output has the same split**, so ask for the kind you mean rather than taking the
first thing in `bin`:

| `OutputArtifactKind` | What it is for |
| --- | --- |
| `Assembly` | The real output. What a host loads. |
| `ReferenceAssembly` | Public surface, no method bodies, in `obj/…/ref/`. What a compiler wants. |
| `SymbolFile` | The `.pdb`, for mapping a frame back to source. |
| `DependencyManifest` | `.deps.json` — what a runtime environment needs to resolve. |
| `RuntimeConfiguration` | `.runtimeconfig.json`, for a project that produces one. |
| `DocumentationFile` | The generated XML, where the project asked for it. |

`Outputs` describes **what a build produces**, not what is on disk now: nothing is checked for
existence, so a project that has never been built still reports where its assembly will land. A kind
is absent only when the build genuinely will not emit it — an ordinary library reports no
`RuntimeConfiguration`, and a project built with `DebugType=none` reports no `SymbolFile`.

Every artifact carries the `TargetFramework` it belongs to, because a cross-targeting project
produces one set per framework and the path alone does not say which build it came from.

## When does a snapshot stop being true

`ProjectSnapshot.EvaluationInputs` is the answer: the project file, the imports that belong to you,
and the restore output. A change to any of them means that project has to be evaluated again; a
change to anything else does not.

It is deliberately not "every file the project imports". A restored project in this repository
imports 137 files — 94 under the SDK, 16 workload manifests beside it, 22 pieces of build logic
inside NuGet packages, and **three** that anybody would edit. The provider drops the toolchain's own
files, because none of them change while a solution is open and watching them would bury the ones
that do.

Some entries name files that are not there, and that is the point: a project with no restore output
still lists where it would be, so a restore *creating* it registers as a change.

The same reasoning covers the files MSBuild finds by looking rather than by being told. A
`Directory.Build.props` is found by walking up from the project, so the list names every directory
that walk would visit — up to and including the one holding the file in use, because MSBuild stops
at the first it finds and a nearer file takes over. Creating one is then a change to something the
project said it depended on, which it previously was not. How far the walk goes when nothing was
found is the provider's judgement; the MSBuild one stops at the opened solution or project. See
[ADR 0019](../adr/0019-a-file-that-is-not-there-yet-is-an-evaluation-input.md).

```csharp
foreach (CanonicalPath input in project.EvaluationInputs)
{
    // watch it; when it changes, refresh
}
```

Given a batch of changes, the snapshot works out what they mean:

```csharp
WorkspaceInvalidation invalidation = snapshot.Invalidate(changedPaths);

switch (invalidation.Scope)
{
    case WorkspaceInvalidationScope.None:        break;                  // the common case
    case WorkspaceInvalidationScope.Projects:    /* invalidation.Projects are stale */ break;
    case WorkspaceInvalidationScope.EntryPoint:  /* load it again */      break;
}
```

`Causes` carries the changes that actually mattered, so a host can say *why* it reloaded rather than
just that it did.

A file system does not report one change per edit — a save produces two or three notifications, a
branch switch produces thousands — so changes go through a coalescer first. It delivers once the
changes have stopped for `QuietPeriod`, **or** once `MaximumDelay` has passed since the first of
them, whichever comes first. Both exist because either alone misbehaves: a quiet period never fires
during a branch switch, and a ceiling alone fires on a fixed schedule in the middle of one.
`Flush()` delivers immediately, for a host that knows the burst is over. The clock is a
`TimeProvider`, so a test advances it by hand rather than waiting.

**Staleness does not spread along project references,** which looks like an omission and is a
finding: evaluating a project does not read the projects it references — MSBuild resolves a
`ProjectReference` during a build, not during an evaluation. So a change to `Library` leaves `App`'s
snapshot correct in every field, and re-evaluating it would reproduce what it already said. Build
freshness *is* transitive; it is a different graph over different inputs, and it is not this one.
[ADR 0015](../adr/0015-invalidation-is-not-transitive.md) has the reasoning.

## Refreshing when files change

Four pieces, composed by you. **Nothing refreshes on its own** — the workspace does not watch, and
[ADR 0016](../adr/0016-watching-belongs-with-the-provider.md) says why: starting an asynchronous
refresh from a timer callback is fire-and-forget, and a failure would have nowhere to go. You have a
caller, a token, and a better idea of whether now is a good moment.

```csharp
var coalescer = new FileChangeCoalescer(async void (batch) =>
{
    SolutionSnapshot? current = workspace.CurrentSnapshot;

    if (current is null || current.Invalidate(batch).IsEmpty)
    {
        return;                                  // the common case: source changed, nothing else
    }

    await workspace.RefreshAsync(token);         // your call, your token, your error handling
});

var watcher = new ProjectFileWatcher(coalescer.Add);

// Re-arm after every publication: a refresh produces new inputs, and a project that stopped
// importing something should stop hearing about it.
workspace.SnapshotChanged += (_, e) =>
    watcher.Watch(e.Snapshot.Projects.SelectMany(p => p.EvaluationInputs));
```

`ProjectFileWatcher` lives in the MSBuild package because doing it correctly needs to know how
projects evaluate. Three things it handles that are easy to get wrong alone:

- **It watches directories, not files.** A watcher bound to one file goes deaf when that file is
  replaced rather than edited — which is what atomic saves do — and can never see it appear.
- **A missing directory is watched through its nearest existing ancestor.** An unrestored project
  names `obj/project.assets.json` and has no `obj`, so without this the change that most needs
  noticing would be the one that never arrived.
- **A buffer overflow reports everything watched.** The OS notification buffer is finite and a
  branch switch exhausts it; the lost changes are unknowable, so assuming they all changed is the
  only answer that cannot miss one silently.

It therefore reports files nothing cares about, deliberately — filtering is `Invalidate`'s job.

**A host that already observes files should use its own** and skip the watcher: feed paths straight
to `coalescer.Add`. That is the expected case for an IDE, not a fallback, which is why the coalescer
takes core types — a path, or a `FileChange` that says what happened to it — rather than any
watcher's event type.

Project identities survive a refresh, so anything you remember about a project — an expanded node,
an open editor, a cached analysis — stays valid across one.

## Telling a saved file from a new one

`Invalidate` answers one question — which evaluations are stale — and a host that shows files has
four more: which open documents to read again, which projects may include different files now,
which documents moved, and whether changes were lost. Bare paths cannot answer them, so a change can
carry its kind ([ADR 0025](../adr/0025-file-changes-carry-their-kind-and-the-snapshot-classifies-them.md)):

```csharp
using var coalescer = FileChangeCoalescer.ForChanges(batch =>
{
    WorkspaceChangeSet changes = workspace.CurrentSnapshot!.Classify(batch);

    if (changes.IsEmpty)
    {
        return;                                   // a build writing its output, an editor's state
    }

    foreach (ProjectItemChange edited in changes.ItemsEdited)
    {
        // edited.Item.FullPath was saved: read the open document again
    }

    foreach (FileRename rename in changes.Renames)
    {
        // whatever had rename.OldPath open follows rename.NewPath
    }

    // changes.ProjectsToEvaluate: the stale projects, then those whose files may differ.
    // changes.RequiresRescan: changes were lost — look at everything again.
});

// The MSBuild package's watcher reports every change with its kind; re-arm it on every
// publication, as ProjectFileWatcher is.
var watcher = new ProjectSourceWatcher(coalescer.Add);
workspace.SnapshotChanged += (_, e) => watcher.Watch(e.Snapshot);

// Or from whatever observes the files already: an IDE's own events.
coalescer.Add(new FileChange(path, FileChangeKind.Created));
coalescer.Add(FileChange.Renamed(oldPath, newPath));
coalescer.Add(FileChange.Overflow);
```

`ProjectSourceWatcher` watches each project's directory with everything below it, and the files
outside them that the snapshot names — the solution, imports above the projects, linked files —
through their own directories; it watches what `ProjectFileWatcher` would as well, so a host needs
one or the other. A build's own writes come with it, and `Classify` leaves them out.

**The coalescer nets each path over the batch**, because only its first and last state matter: a
file created and deleted inside a batch never happened, one deleted and created again was replaced.
That is what makes an editor's atomic save — the text to a temporary file, the original renamed
aside, the temporary renamed over it, the original deleted, which is how JetBrains Rider saves —
arrive as one `Changed` of the saved file instead of five events that each look like the project
gaining or losing a file. Renames in a chain are one rename; a lost change comes first. A file moved
to another folder reaches a watcher as a deletion and a creation — Windows reports it so even inside
one watch — and one of each with the same name in a batch is that move; with two of either, which
went where is not known, and the batch says what it saw.

**`Classify` asks every change each question on its own**, because one file can be several things —
a `Directory.Build.props` beside a project is an import its evaluation read and an item its globs
took:

| Answer | When |
| --- | --- |
| `RequiresRescan` | changes were lost — `FileChangeKind.Overflow` |
| `Invalidation` | an evaluation input changed, appeared, went away or was renamed — `Invalidate`'s answer for those inputs |
| `ItemsEdited` | the contents of a file a project declares changed; that alone makes nothing stale |
| `MembershipChanged` | a path appeared, went away or was renamed where a project's globs reach, or a declared file did wherever it is; only an evaluation can say what the globs take now |
| `Renames` | a declared file moved — with the directory it was in, too |

Where globs reach is inside the project's directory, outside its `BuildDirectories` — where a build
writes, `bin` and `obj` unless the project moved them — and outside directories below it whose names
start with a dot. A path may be a directory: a watcher reports a folder renamed or deleted as one
change, and the snapshot alone knows what was below it, so that is what the change stands for — its
inputs are stale, its declared files are their projects' to lose, and a folder renamed moves every
declared file in it.

`TryGetItem` is the lookup behind it — the item a file is, and the project that declares it — on an
index built once per snapshot, so asking it for every save of every file costs a dictionary lookup.

## Changing what a project references

`ArxisStudio.ProjectSystem.NuGet` writes project files. It sits **beside** the MSBuild provider
rather than above it and references neither MSBuild nor it: reading a project and writing one are
different jobs, and a package that could do both would eventually disagree with itself.

```csharp
ProjectOperationResult result = await PackageEditor.ApplyAsync(
    new PackageEditRequest
    {
        Kind = PackageEditKind.Install,
        ProjectFilePath = project.ProjectFilePath,
        PackageId = "Serilog",
        Version = "4.1.0",
    },
    PackageVersionLayout.From(project));
```

`PackageVersionLayout` is the one thing that cannot be guessed. Under **central package management**
the project declares `<PackageReference Include="Serilog" />` with no version and the version lives
in `Directory.Packages.props`; without it the version is an attribute on the reference. Writing it
in the wrong place produces a file that either does not restore or silently keeps the old version.
`From(project)` reads both facts off a loaded snapshot, so you do not have to know either property
name.

One change can therefore touch two files, and it touches them together: **if the second write fails,
the first is put back.** A half-applied edit leaves a reference with no version, which is a
repository that does not restore.

Edits are surgical. Existing lines keep their exact text, comments and blank lines survive, a new
item copies the indentation and newline of what it sits beside, and a new item is sorted into a list
only when that list is already sorted — imposing an order on a list somebody grouped by meaning
rearranges their file as a side effect of adding one line.
[ADR 0017](../adr/0017-project-files-are-edited-as-xml.md) has the reasoning.

Three things it deliberately does not do:

- **Restore.** Changing a file changes disk, not the model. Restore through the workspace and
  refresh when you want the snapshot to catch up.
- **Validate the version.** It is written verbatim, because `[1.0,2.0)` and `1.0.0-*` are both
  meaningful and normalising them would change what the project asked for.
- **Remove a central version on uninstall.** `Directory.Packages.props` is shared by every project
  in the repository and this edit was given one, so it cannot see who else still needs the pin.

An edit with nothing to do — installing something already referenced, removing something absent —
succeeds and says so with `APS4004` as a warning. A silent no-op looks exactly like a successful
change.

### Finding a package and choosing a version

```csharp
using var client = new HttpClient();
using var feed = new NuGetHttpFeed(client);          // nuget.org unless told otherwise

FeedResult<FoundPackage> found = await feed.SearchAsync(
    new PackageSearchRequest { Query = "avalonia" }, token);

if (found.HasErrors) { /* the feed did not answer; found.Diagnostics says why */ }
```

`FeedResult<T>` exists to keep two answers apart: **nothing matched** and **nobody answered**. An
empty list for both would let a tool report that a package has no versions when in fact the network
is down, and those call for opposite responses.

```csharp
FeedResult<string> versions = await feed.GetVersionsAsync("Serilog", token);

string? target = PackageVersions.Latest(versions.Items);                      // newest stable
string? preview = PackageVersions.Latest(versions.Items, includePrerelease: true);
```

`PackageVersions` orders and chooses; versions cross the boundary as the strings a project file
holds, never as a NuGet type. Prereleases are excluded by default, and a package whose only versions
are prereleases returns `null` rather than quietly offering an alpha to somebody who asked for "the
latest". Inside, the comparison is NuGet's own — a prerelease sorts *before* the release it
precedes, `beta.9` before `beta.10`, build metadata is ignored, and there is a fourth numeric field.

### What the feed knows against a version

```csharp
FeedResult<PackageVersionMetadata> metadata = await feed.GetMetadataAsync("Serilog", token);

foreach (PackageVersionMetadata version in metadata.Items.Where(v => v.HasWarnings))
{
    // version.Deprecation?.AlternatePackageId, version.Vulnerabilities[0].Severity, …
}
```

A separate call from `GetVersionsAsync` because it costs more: a version list is one small document
and this is the package's whole registration, which for a long-lived package arrives as several. A
caller offering a choice of versions should not pay for that; a caller about to install one should.

Two details are measured rather than assumed. A severity arrives as a **number spelled as a string**
and is named here, with anything unrecognised becoming `Unknown` rather than the least serious value.
And only the SemVer 2.0.0 registration (`RegistrationsBaseUrl/3.6.0`) carries deprecation and
advisories at all — the unversioned `RegistrationsBaseUrl` that every feed advertises returns the
same packages with those fields simply absent, so a feed offering only that one is refused with a
diagnostic instead of read. Silence must not be able to read as reassurance.

### Installing and restoring as one operation

`PackageEditor` writes files and stops. `PackageInstaller` is the whole thing a person means by
"install this package":

```csharp
ProjectOperationResult result = await PackageInstaller.ApplyAndRestoreAsync(
    new PackageEditRequest
    {
        Kind = PackageEditKind.Install,
        ProjectFilePath = project.ProjectFilePath,
        PackageId = "Serilog",
        Version = PackageVersions.Latest(versions.Items),
    },
    workspace,
    PackageVersionLayout.From(project));
```

**If the restore fails, the change is put back.** That is what `dotnet add package` does and for the
same reason: a project carrying a reference that will not restore does not build, and whoever asked
for the package is not better off for having half of it. The restore's own diagnostics survive the
undo, because *why* it failed is the actionable part — a version that does not exist reads very
differently from a feed that was unreachable. `APS4008` says the change was undone; `APS4009` is the
bad case where it could not be.

An edit that changes nothing does not restore: installing a package the project already has is a
no-op, and a restore for a file nobody touched is a wait nobody asked for.

**Nothing here evaluates a project.** The restore goes through the workspace, which routes it to
whichever provider can run one, and this package references no build engine — an architecture test
checks that in both directions and against the compiled assembly, because the moment the package
manager could evaluate, something in it would, and then two packages would read project files from
two engines and eventually disagree. Restoring is still not a mutation, so nothing is published and
the version does not advance; call `RefreshAsync` when you want the model to catch up.

`IPackageFeed` is an interface because feeds vary in ways this library should not try to contain.
`NuGetHttpFeed` speaks enough V3 to search a public feed, and deliberately **does not read
`NuGet.config` and does not authenticate** — discovering configured sources is hierarchical and
private feeds need NuGet's credential-provider model, neither of which is safe to half-implement,
because a half-implementation fails by silently not finding a package. A host with private sources
implements the interface over its own client. That seam is also what lets everything above it be
tested without a network.

`ResolvedPackages` is empty when nothing has been restored, which is not the same as a project having
no packages. A project that declares packages and has no restore output says so with `APS2005`, as a
warning rather than a failure.

Nothing here checks that the restore output is current. If a `PackageReference` changed since the
last restore, this reports the previous resolution — see
[known limitations](../limitations.md).

## Running restore and build

Executing is a **second, optional capability**. `IProjectSystemProvider` reads projects;
`IProjectOperationProvider` does things to them, and a provider may implement one, the other, or
both. `MSBuildProjectProvider` implements both.

```csharp
ProjectOperationResult result = await workspace.ExecuteAsync(
    new ProjectOperationRequest
    {
        Kind = ProjectOperationKind.Build,
        Workspace = workspace.Identity,
        EntryPointPath = CanonicalPath.Create(@"C:\src\App\App.sln"),
        Configuration = "Release",
    },
    new Progress<ProjectOperationProgress>(p => Console.WriteLine(p.Message)));
```

`Kind` is `Restore`, `Build`, `Rebuild` or `Clean`. `Configuration`, `Platform` and `TargetFramework`
become global properties, and `GlobalProperties` carries anything else. Asking for something no
provider can do is `APS1004` — an ordinary diagnostic, because a read-only provider is a legitimate
configuration rather than a broken one.

Three things about this that differ from loading, all of them deliberate and recorded in
[ADR 0014](../adr/0014-an-operation-is-not-a-mutation.md):

- **It does not take the mutation boundary.** A build takes minutes, and stalling every read behind
  one would freeze a designer for the length of the build. Loads and refreshes run alongside it.
- **It publishes nothing and does not advance the version.** A build changed what is on disk, not
  what the workspace knows — nothing was re-read. Call `RefreshAsync` yourself when you want the
  model to catch up. This is why a version increment always means the same thing.
- **`DisposeAsync` does not wait for it.** There is no workspace state for a running operation to
  corrupt, so disposal returns at once and the build finishes on its own. A host that needs quiet
  before exiting tracks its own operations.

The MSBuild provider serialises builds behind a process-wide lock, because
`BuildManager.DefaultBuildManager` is a singleton: **one build at a time per process**. Parallelism
across projects still happens — MSBuild's own scheduler provides it inside one build.

Cancellation reaches the engine, which abandons its submissions. It always surfaces as
`OperationCanceledException`, never as a failed result — but a target that had already run has
already run, so files written before the cancellation stay written.

### Building beside an IDE

A tool that builds the projects an IDE has open — a designer — must not build into the IDE's
folders: the application the IDE started locks the output (`MSB3027`), and two builds started
together write one intermediate folder. `MSBuildDesignOutput.GlobalProperties` sends the output and
the intermediate files to `bin/ArxisStudio/` and `obj/ArxisStudio/` of each project, and leaves the
base paths — and with them the SDK's default excludes and the shared restore — alone
([ADR 0026](../adr/0026-a-design-build-writes-beside-the-ides-never-over-it.md)). Pass it to the
loads as well as to the operations, so that the snapshot's `Outputs` name what the build writes:

```csharp
var load = new WorkspaceLoadRequest
{
    Workspace = workspace.Identity,
    EntryPointPath = solution,
    GlobalProperties = MSBuildDesignOutput.GlobalProperties,
};

var build = new ProjectOperationRequest
{
    Kind = ProjectOperationKind.Build,
    Workspace = workspace.Identity,
    EntryPointPath = solution,
    GlobalProperties = MSBuildDesignOutput.GlobalProperties,
    Projects = [app.Identity],
};
```

A global output path does not get the framework folder the SDK appends otherwise, so a design build
of a multi-targeted project names one framework (`TargetFramework` on both requests).

Whether a restore has to run first is the host's to decide, from what the snapshot says: there is no
restore output on disk yet, or an evaluation input changed that is not one of `RestoreOutputs` — the
assets file and the imports restore generates. Restore's own output changing is a restore having
run; restoring again for it would never stop.

```csharp
bool neverRestored = !project.RestoreOutputs.IsEmpty
    && !project.RestoreOutputs.Any(output => File.Exists(output.Value));

bool inputsMoved = changes.Invalidation.Causes.Any(cause =>
    project.EvaluationInputs.Contains(cause) && !project.RestoreOutputs.Contains(cause));
```

A project whose provider names no restore output — one without restore at all — is never restored.

## Results and diagnostics

`WorkspaceLoadResult.Status` is computed from whether there is a snapshot and whether anything
errored. There is no `Success` property to disagree with the evidence, and a solution that loaded
with one broken project reports `SucceededWithErrors` — never `Succeeded`. See
[ADR 0008](../adr/0008-a-result-state-computed-not-declared.md).

| `Status` | Meaning |
| --- | --- |
| `Succeeded` | A snapshot, and nothing went wrong |
| `SucceededWithErrors` | A usable snapshot, and something in it failed |
| `Failed` | No usable snapshot; `Snapshot` is `null` |

`ProjectOperationResult.Status` follows the same discipline with only two outcomes — `Succeeded` or
`Failed` — because a build either produced its outputs or did not. The constructors enforce the
agreement in both directions: `Succeeded` with an error diagnostic throws, and so does `Failed`
without one. Warnings do not fail an operation.

Ordinary problems are `ProjectDiagnostic` values with a stable code, never exceptions. Branch on
`Code`, never on `Message` — the message is for people and may be reworded.

| Range | Raised by |
| --- | --- |
| `APS1xxx` | Core: requests, paths, snapshots, workspace, invariants |
| `APS2xxx` | MSBuild discovery and evaluation |
| `APS3xxx` | Restore and build execution |
| `APS4xxx` | Package-management operations |
| `APS5xxx` | Integration adapters |

The ranges divide by concern rather than by assembly, so the MSBuild package raises `APS2xxx` for
evaluating a project and `APS3xxx` for building one: a consumer routing on "the build failed" should
not have to know which package happened to run it.

The core implements four: `APS1001` (no provider can open this), `APS1002` (a provider threw),
`APS1003` (a provider returned something that breaks the boundary's contract), `APS1004` (no
provider can do that to a project). Codes with no producer are not declared.

Most of what a failed build reports arrives under **the engine's own codes** — `CS0103`, `MSB3021` —
rather than an `APS` one, attributed to the file and line the engine named. See
[ADR 0013](../adr/0013-a-provider-may-keep-its-engines-diagnostic-codes.md).

Exceptions are reserved for invalid API use, cancellation, disposed objects and broken invariants.
Cancellation is always `OperationCanceledException`, never a diagnostic.

## Threading and ordering

Reading `CurrentSnapshot` is lock-free and safe from any thread, including after disposal.

Loads and refreshes take one mutation boundary **in the order the requests arrive** — a queue, not a
semaphore, so a slow older refresh can never overwrite a newer one. There is no coalescing:
*N* refreshes run *N* provider loads. See
[ADR 0006](../adr/0006-one-fifo-mutation-boundary.md).

Two things about `SnapshotChanged` that are easy to get wrong:

- **A throwing handler is isolated.** Every other handler still runs and the exception never reaches
  the caller of the load — which also means nothing reports it. Catch your own.
- **Delivery is not ordered.** The boundary is released before the raise, so the next publication can
  overlap the previous notification. Publication order is strict; delivery order is not. A handler
  keeping derived state must compare `e.Version` and ignore anything older.

Both are recorded in [ADR 0007](../adr/0007-a-throwing-subscriber-is-isolated.md) and
[docs/limitations.md](../limitations.md).

`DisposeAsync` waits for work already inside the boundary to finish — that is the only reason it is
asynchronous. Work queued behind disposal gets `ObjectDisposedException`. Providers are not disposed;
the workspace did not create them.

None of this applies to `ExecuteAsync`, which never takes the boundary: builds run alongside loads,
in no particular order relative to them, and disposal does not wait for one.

## Writing a provider

The contract, in the order it matters:

1. **Report problems as diagnostics, not exceptions.** A missing file, an unparseable project, an
   unresolved reference — all of those belong in a `WorkspaceLoadResult`. A provider that throws is
   caught and reported as `APS1002`; the exception does not reach the caller.
2. **Let cancellation propagate.** It is the one thing that must not become a diagnostic.
3. **Build with the builders.** They are what turn provider-owned state into core-owned immutable
   state, and they are the only way to construct a snapshot.
4. **Leave `Version` alone.** The workspace stamps it, because only the workspace knows whether the
   snapshot was published.
5. **Mint identities from the request.** `request.Workspace` is there so a provider never has to call
   back into the workspace for one.
6. **Keep `CanLoad` cheap.** No file system, no blocking; the workspace asks every provider in turn.

The workspace checks what comes back and refuses a snapshot stamped with another workspace's
identity, one answering a different entry point, or one containing duplicate project identities —
each as `APS1003`. Those are the shapes that would silently corrupt identity determinism.

Implementing `IProjectOperationProvider` as well is optional and follows the same first two rules —
diagnostics rather than exceptions, and let cancellation through. Two more are specific to it:
**answer `CanExecute` honestly**, since the workspace routes on it and a wrong answer turns into a
failure a caller cannot interpret; and **never return `null`**, which is `APS1003` like any other
broken result.

## Loading XAML against a project

`ArxisStudio.ProjectSystem.Markup.Xaml` is the adapter onto `ArxisStudio.Markup`. It is the one
package in this family allowed near Markup and Avalonia, and nothing in the family depends on it —
[ADR 0018](../adr/0018-the-adapter-references-markup-by-source.md).

```csharp
(XamlLoadEnvironment environment, ProjectAssemblyContext assemblies) =
    ProjectXamlEnvironment.CreateFor(snapshot, project);
```

That gives Markup everything it needs: the assemblies the project's documents may name, the types
behind them, `avares://` resolved to **the project's own files rather than the last build** — which
is what makes an edit visible without rebuilding — and documents openable by the `avares` URI a
`StyleInclude` names them with, which Markup's own file provider cannot do because it answers
`file:` and nothing else.

Both come from one `ProjectResourceMap`, so resolving a resource and opening a document cannot
disagree about which file a URI means.

Only what gets rebuilt goes into that collectible context. Packages and anything the host already
loaded stay with the host, because two copies of Avalonia produce two `Button` types that are not
assignable to one another. And assemblies are read into memory rather than loaded from their path,
so the next build can overwrite the file it just loaded.

A designer keeps **one generation at a time**. The naive swap — dispose on staleness, `CreateFor`
again — was measured out of existence in
[ADR 0021](../adr/0021-a-run-holds-one-generation-of-a-projects-types.md): a superseded generation
was unloaded but never collected, and the runtime XAML compiler went on answering `x:Class` with
the first copy it saw. `IsCurrentFor(snapshot)` says whether the *model* moved on and
`IsCurrentOnDisk()` whether a build rewrote the *types*.

What makes a replacement safe is proving the predecessor gone
([ADR 0023](../adr/0023-a-generation-is-reclaimed-before-its-successor-is-born.md)):

```csharp
// Let go first — the environment's type resolver holds this generation's assemblies —
// then close its windows and forms, and only then ask.
environment = null;
assemblies = null;

if (await old.TryReclaimAsync(token))
{
    (environment, assemblies) = ProjectXamlEnvironment.CreateFor(snapshot, project);   // safe: nothing to collide with
}
else
{
    // Something still holds it. A successor here would be a second copy of every type,
    // so the honest answer is a new process.
}
```

**A designer showing forms of several projects keeps one generation for all of them** — a design
set ([ADR 0027](../adr/0027-a-design-set-is-one-generation.md)). A library two projects reference is
then one assembly, and a form of either sees one type. Each project's documents get an environment of
their own, searching what that project's build sees, over a resource map the host keeps current:

```csharp
using ProjectAssemblyContext generation =
    ProjectAssemblyContext.Create(snapshot, [app.Identity, controls.Identity]);   // everything loaded now

// Two files of one name, or a project named like an assembly the host has: said, not thrown.
foreach (ProjectDiagnostic diagnostic in generation.Diagnostics) { /* APS5001, APS5002 */ }

var members = new XamlMemberResolver();   // one per generation
XamlLoadEnvironment environment = ProjectXamlEnvironment.Create(
    generation, app.Identity, () => currentMap, members: members);
XamlLoadOptions options = ProjectXamlEnvironment.CreateOptions(generation, app.Identity);
```

Create the generation when no build is running: it reads every output before it returns, so it is
one build of each.

What does not need the restart is markup, including the markup of a placed control.
`ProjectXamlPopulation` pairs a project's documents with the generation's types by `x:Class` and
keeps new instances populated from the document as it is now —
[ADR 0022](../adr/0022-an-embedded-controls-markup-follows-the-live-document.md):

```csharp
using ProjectXamlPopulation population = ProjectXamlPopulation.Create(assemblies, environment);

// On open, after every applied edit, and after every reload from disk:
await population.SetDocumentAsync(form.Document, token);

// Then rebuild the open previews that place the control; their fresh instances
// land on the registered document. Dispose the registry before the context.
```

`ProjectMarkupDiagnostics` carries diagnostics across, so a tool shows one list:

```csharp
ImmutableArray<ProjectDiagnostic> shown =
    ProjectMarkupDiagnostics.ToProject(markupDiagnostics, documentText, filePath);
```

Supply the text. Markup measures a span as an offset; the project model measures it as lines and
columns; neither converts without it, and without it the translation drops the position rather than
inventing one.

## A designer beside an IDE

Everything above is a primitive, and the order they are used in is where a designer goes wrong
silently — a participant still holding a root, a window nobody closed, an open landing between two
generations, and the reclaim honestly answers "held". `ProjectDesignHost` is that order, once
([ADR 0028](../adr/0028-the-design-host-replaces-a-generation-in-order.md)):

```csharp
var options = new ProjectDesignHostOptions
{
    BuildProperties = MSBuildDesignOutput.GlobalProperties,   // builds never write over the IDE's
    ExternalEditDescription = "Изменено вне дизайнера",       // a history step, in the designer's language
    ReleaseHostState = ClearFocusAsync,                       // the designer's own state, let go last
};

await workspace.LoadAsync(options.CreateLoadRequest(workspace, solutionPath), token);

await using var host = new ProjectDesignHost(workspace, options);
await host.StartAsync(token);       // builds what is older than its sources, then one generation

// The host does not watch: the owner composes watching (ADR 0016) and feeds it batches.
using FileChangeCoalescer coalescer = FileChangeCoalescer.ForChanges(batch => host.NotifyChanged(batch));
using var watcher = new ProjectSourceWatcher(coalescer.Add);

watcher.Watch(workspace.CurrentSnapshot!);   // and again on every snapshot the workspace publishes
```

The load must carry the build properties — the host refuses a snapshot that names the IDE's outputs,
because a generation of those would never see a design build — and `CreateLoadRequest` adds them with
the evaluated properties the host reads (`IsTestProject`, which keeps test projects out of the design
set).

**Documents** are opened through the host, which gives each a history of its own, its `avares` URI and
its project's environment, and attaches it to the live generation — or, during a swap, waits for the
successor:

```csharp
XamlLiveDocument form = await host.OpenDocumentAsync(file, new ProjectDesignDocumentOptions { RootAccess = card }, token);

host.SetVisibleDocuments([form]);          // a swap attaches these; the rest on EnsureLiveAsync
await host.EnsureLiveAsync(other, token);  // a tab is shown
await host.SaveAsync(form, token);         // in the encoding it was read in; the watcher's echo is no conflict
await host.CloseDocumentAsync(form, token);
```

What the other editor does arrives as events, on the user interface thread: a saved form is a step
of its history (`ExternalEditDescription`) and is shown at once; over unsaved edits it is
`ExternalConflict` and nothing changes until the person picks (`AcceptExternalTextAsync` with
`TakeTheirs` or `KeepMine`); a renamed form follows its file (`DocumentMoved`); a deleted one stays
open and says so (`DocumentDeleted`). A saved control is shown in every form that places it, open or
not — in place: the elements that place it are built again, and the form keeps its session and root.

**Saved code is built** once the code has been quiet for `BuildDelay`, of the top projects of what
changed, restoring first when a restore is due (`BuildCompleted`). A build that rewrote what the
generation was loaded from makes it stale, and so does a change to what the design set is made of.

**A swap waits only for the designer.** Whatever a swap would cut defers it:

```csharp
using (host.Gate.Defer("dragging on the canvas"))
{
    // the gesture; the swap runs the moment the last deferral is disposed
}
```

and whatever holds anything of a generation — roots on a canvas, a selection, an inspector's members
— registers as a participant, lets go when asked and takes up the successor:

```csharp
sealed class CanvasParticipant(DesignCanvas canvas, ProjectDesignHost host) : IProjectDesignParticipant
{
    private IDisposable? _frozen;

    public ValueTask ReleaseAsync(CancellationToken token)
    {
        _frozen = canvas.Freeze();     // the person sees the last frame, not an empty canvas
        canvas.TakeRootsOff();         // and nothing on it holds the generation
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreAsync(CancellationToken token)
    {
        canvas.ShowRootsOf(host.Documents);
        _frozen?.Dispose();
        return ValueTask.CompletedTask;
    }
}

using IDisposable registration = host.Register(new CanvasParticipant(canvas, host));
```

`SwapCompleted` reports the phases — release, teardown, reclaim, rebuild — once the host is out of the
swap. **When the generation would not go**, there is no successor: `RestartRequired` with
`GenerationStillHeld`, the documents detached with their text and unsaved edits, the participants
holding the frame they froze. A package the generation loaded that moved to another file is
`PackagesChanged`. Either way only a new process shows the types as they are now; the designer hands
its session over and reopens each document with `ProjectDesignDocumentOptions.Text` and `SavedText`,
then `ReloadAsync`, which turns a file that moved on in the meantime into a conflict rather than an
overwrite.

One host per project per process: a second one over the same assemblies waits for the first one's
generation to go, and requires a restart when it does not
([ADR 0029](../adr/0029-a-held-generation-may-be-asked-again.md)).

**A designer inside an IDE** does not own the workspace: the IDE's project service evaluates and builds
on its own queue, and writes the solution's files through its own service, which keeps a local history
and knows its own writes from other editors'. The host then reads and builds through the owner's
service, and saves through its file service
([ADR 0032](../adr/0032-a-design-host-reads-builds-and-saves-through-its-owner.md)):

```csharp
sealed class IdeSource(IdeProjects projects) : IProjectDesignSource
{
    public SolutionSnapshot? Snapshot => projects.DesignSnapshot;      // evaluated with the build properties
    public event EventHandler? SnapshotChanged { add => projects.DesignChanged += value; remove => projects.DesignChanged -= value; }
    public ValueTask RefreshAsync(CancellationToken token) => projects.RereadAsync(token);
    public ValueTask<ProjectOperationResult> ExecuteAsync(
        ProjectOperationRequest request, IProgress<ProjectOperationProgress>? progress, CancellationToken token) =>
        projects.RunOnTheQueueAsync(request, progress, token);          // as given: the host decides when to restore
}

sealed class IdeWriter(IdeFiles files) : IProjectDesignWriter
{
    public ValueTask WriteAsync(CanonicalPath file, SourceText text, CancellationToken token) =>
        files.WriteAsync(file, text, token);                            // history, its own echo, a refusal over news
}

await using var host = new ProjectDesignHost(new IdeSource(projects), options with { Writer = new IdeWriter(files) });
```

`ProjectDesignHost(workspace, options)` is `ProjectDesignHost(ProjectDesignSource.From(workspace),
options)`, and a host with no `Writer` writes the file in place, as it always did. A writer that throws
leaves the document unsaved.

**A form is dressed by its program's application** — the theme `App.axaml` sets and the resources it
declares — not by the designer's. The host loads them for each form, as written: the program's `App`
class is never constructed ([ADR 0033](../adr/0033-a-form-is-dressed-by-its-programs-application.md)):

```csharp
await using ProjectDesignApplication? application = await host.OpenApplicationAsync(form, token);

if (application?.Application is { } declared)        // null: no application in the project or above it
{
    // A style and a dictionary have one owner: the form takes them over, and the next form opens its own.
    IStyle[] styles = [.. declared.Styles];
    IResourceDictionary resources = declared.Resources;

    declared.Styles.Clear();
    declared.Resources = new ResourceDictionary();

    formScope.Styles.AddRange(styles);
    formScope.Resources.MergedDictionaries.Add(resources);
    formScope.RequestedThemeVariant = application.RequestedThemeVariant;
}
```

**A project on another Avalonia is not loaded.** The design set's resolved `Avalonia` is measured against
the process's before every generation ([ADR 0034](../adr/0034-a-project-on-another-avalonia-is-not-loaded.md)):
another major version is `Unsupported` — no generation, no builds, documents with their text, and
`UnsupportedReason` saying which project is on which version — and another minor version a warning in
`GenerationDiagnostics`. A snapshot naming the version the designer runs lifts it without a restart.

A library's form is dressed by the application of the first project of the design set that references
the library. What an application declares may be of the project's types: let go of it when the host asks
the designer to let go for a swap. A swap closes every application still open, a closed one holds nothing,
and a saved application document is `ApplicationChanged` — open again.

**A toolbox of the project's controls** is a listing by name — what the live generation built of a
project and what it references, and the `x:Class` documents among them no build has produced yet
([ADR 0031](../adr/0031-a-toolbox-lists-controls-by-name-and-builds-through-the-gate.md)):

```csharp
ImmutableArray<ProjectControlInfo> controls = await host.GetPlaceableControlsAsync(project, token);

// ClassName, Name, XmlNamespace, SuggestedPrefix, Kinds, Project, Document, IsBuilt — and no Type,
// so the toolbox outlives the generation it was read from without holding it.
```

A control the IDE has just written is listed with `IsBuilt` false, in `using:` its namespace; a
document whose root is a window, an application or anything else that is not a control is not listed.
Placing an unbuilt one builds it first:

```csharp
// What the drop meant, as names: the swap that follows the build rebuilds every form.
(CanonicalPath file, XamlElementPath parent, int index) intent = (form.File, XamlElementPath.Of(parentElement), index);

if (await host.EnsureBuiltAsync(control, token))   // builds, then waits for the swap through the gate
{
    // the live generation has the class: insert the element at the intent, found again by its path
}
```

The data a form binds to is offered the same way — `GetTypeCatalogAsync(project)` is Markup's catalog
of the project and what it references, by name, and its entries of kind `Data` are the view models a
document can take as its data type.

`EnsureBuiltAsync` answers at once for a class the generation has, and otherwise builds the control's
project and waits for the generation the build calls for — through the gate, never past a deferral, so
a drop made during a gesture is placed when the gesture lets go. `false` is a failed build, a class the
build did not produce, or a restart required; `BuildCompleted` and `RestartRequired` say which.

## Further reading

- [Known limitations](../limitations.md) — the honest list; read it before promising anything.
- [Architecture decisions](../adr) — why the awkward parts are the way they are.

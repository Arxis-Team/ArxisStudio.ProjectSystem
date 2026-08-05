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
takes bare paths rather than any watcher's event type.

Project identities survive a refresh, so anything you remember about a project — an expanded node,
an open editor, a cached analysis — stays valid across one.

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
| `APS4xxx` | NuGet package-management operations |
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

## Further reading

- [Known limitations](../limitations.md) — the honest list; read it before promising anything.
- [Architecture decisions](../adr) — why the awkward parts are the way they are.

# ArxisStudio.ProjectSystem — API guide

A model of a .NET solution that is safe to read from any thread while something else reloads it, and
that does not drag a build engine into your process.

Written against **v0.1.0-preview.1**. Every example uses only published API.

## What this package is, and is not

It defines the model, the diagnostics, the provider boundary, and the workspace that publishes
snapshots. It **reads nothing**: there is no `.sln`, `.slnx` or `.csproj` parsing here and no MSBuild
evaluation. Data arrives through a provider, and the first real one —
`ArxisStudio.ProjectSystem.MSBuild` — is Milestone 1.

Until then, the way to use this package is to implement `IProjectSystemProvider` yourself. That is
also the way to test a tool built on it without an MSBuild workload on the machine, which is worth
having permanently.

## The shape of it

```text
ProjectWorkspace          owns the current snapshot, its version, and the order of changes
    └── SolutionSnapshot  one immutable picture of everything open, at one version
            └── ProjectSnapshot   one project: references, items, outputs, diagnostics
```

`IProjectSystemProvider` is what fills that in. The workspace calls it, checks what comes back, and
publishes.

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

Ordinary problems are `ProjectDiagnostic` values with a stable code, never exceptions. Branch on
`Code`, never on `Message` — the message is for people and may be reworded.

| Range | Raised by |
| --- | --- |
| `APS1xxx` | Core: requests, paths, snapshots, workspace, invariants |
| `APS2xxx` | MSBuild discovery and evaluation |
| `APS3xxx` | Restore and build execution |
| `APS4xxx` | NuGet package-management operations |
| `APS5xxx` | Integration adapters |

The core implements three: `APS1001` (no provider can open this), `APS1002` (a provider threw),
`APS1003` (a provider returned something that breaks the boundary's contract). Codes with no
producer are not declared.

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

## Further reading

- [Known limitations](../limitations.md) — the honest list; read it before promising anything.
- [Architecture decisions](../adr) — why the awkward parts are the way they are.

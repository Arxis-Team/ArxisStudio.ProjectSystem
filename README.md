# ArxisStudio.ProjectSystem

Provider-neutral infrastructure for tools that need to understand a .NET solution: what projects
it contains, what they reference, what they target, and where their outputs land — without the
tool taking a dependency on MSBuild, NuGet, Roslyn, or any UI framework.

The audience is IDEs, designers, hot-reload hosts, analyzers, refactoring tools, and
project-management applications. What they share is a need for a *model* of a solution that is
stable, immutable, and safe to read from several threads while something else reloads it.

## Status

**Milestone 6 in progress.** The core is complete, and `ArxisStudio.ProjectSystem.MSBuild` opens a
solution or a standalone project by evaluating it, and runs restore, build, rebuild and clean over
it.

What works today: open a `.sln`, a `.slnx`, or a single `.csproj`, and get an immutable snapshot of
the projects with their target frameworks, configurations, declared references, items, evaluated
properties and output paths — plus the solution's folders and its project graph, with references
between projects resolved to identities. Malformed and missing projects come back as diagnostics
without taking the rest of the solution down with them.

It also reports what restore resolved for the active framework — exact package versions, the
packages that arrived transitively, and the assemblies each contributes, kept apart as compile and
runtime because a package can be compiled against a reference assembly and contribute nothing at run
time. A project that declares packages and has not been restored says so as a warning rather than
failing.

Each project also describes what building it produces: the assembly, its symbols, its reference
assembly, and the `.deps.json` and `.runtimeconfig.json` a runtime environment needs — each present
only when the build genuinely emits it, and each tagged with the framework it belongs to. The
reference assembly and the real output are separate answers because a compiler and a loader want
different files.

And it runs work: restore, build, rebuild and clean go through MSBuild's own engine, reporting
progress as projects start and coming back with the engine's diagnostics attributed to file and
line. A failing build is a failed result carrying `CS0103`, not an exception.

```csharp
ProjectOperationResult result = await workspace.ExecuteAsync(new ProjectOperationRequest
{
    Kind = ProjectOperationKind.Build,
    Workspace = workspace.Identity,
    EntryPointPath = CanonicalPath.Create(@"C:\src\App\App.sln"),
});
```

An operation changes what is on disk, not what the workspace knows, so it publishes no snapshot and
does not advance the version — call `RefreshAsync` when you want the model to catch up.
[ADR 0014](docs/adr/0014-an-operation-is-not-a-mutation.md) says why that is the deliberate answer
rather than an omission.

And it knows when it has gone out of date. Each project says which files it was built from, a
watcher observes them, a coalescer turns a burst of changes into one batch, and the snapshot works
out whether the batch matters:

```csharp
WorkspaceInvalidation invalidation = snapshot.Invalidate(changedPaths);
```

Nothing refreshes on its own — you call `RefreshAsync`, with your own token and your own error
handling. [ADR 0016](docs/adr/0016-watching-belongs-with-the-provider.md) says why that is the
deliberate answer.

And `ArxisStudio.ProjectSystem.NuGet` changes what a project references — install, update, uninstall
— written straight into the project XML, keeping its comments, its blank lines and its indentation.
Under central package management the reference and the version go into two different files, and they
are written together or not at all. It also searches a NuGet V3 feed and orders a package's
versions, so "install the latest stable" is a question it can answer.

What does not yet: reading `NuGet.config` to discover configured sources, authenticating to private
feeds, and staleness detection for build outputs.

## A minimal example

Opening a real project takes two objects:

```csharp
await using var workspace = new ProjectWorkspace(new MSBuildProjectProvider());

WorkspaceLoadResult result = await workspace.LoadAsync(new WorkspaceLoadRequest
{
    Workspace = workspace.Identity,
    EntryPointPath = CanonicalPath.Create(@"C:\src\App\App.csproj"),
});
```

A provider of your own is the other way in, and it stays useful after the MSBuild one exists: it is
how a tool built on this package gets tested without an SDK on the machine.

```csharp
sealed class InMemoryProvider : IProjectSystemProvider
{
    public string Name => "InMemory";

    public bool CanLoad(WorkspaceEntryPoint entryPoint) =>
        entryPoint.Kind == WorkspaceEntryPointKind.Project;

    public ValueTask<WorkspaceLoadResult> LoadAsync(
        WorkspaceLoadRequest request, CancellationToken cancellationToken)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(request.Workspace, request.EntryPointPath),
            Name = request.EntryPointPath.FileName,
            ProjectFilePath = request.EntryPointPath,
            ProviderName = Name,
        };

        project.TargetFrameworks.Add("net10.0");

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = request.Workspace,
            Name = request.EntryPointPath.FileName,
            Request = request,
        };

        solution.Projects.Add(project.ToSnapshot());

        return ValueTask.FromResult(WorkspaceLoadResult.Success(solution.ToSnapshot()));
    }
}
```

```csharp
await using var workspace = new ProjectWorkspace(new InMemoryProvider());

WorkspaceLoadResult result = await workspace.LoadAsync(new WorkspaceLoadRequest
{
    Workspace = workspace.Identity,
    EntryPointPath = CanonicalPath.Create(@"C:\src\App\App.csproj"),
});

foreach (ProjectSnapshot project in result.Snapshot!.Projects)
{
    Console.WriteLine($"{project.Name} -> {string.Join(", ", project.TargetFrameworks)}");
}
```

The [API guide](docs/api/README.md) covers the rest: paths, identity and staleness, results and
diagnostics, ordering, and what a provider must get right.

## Libraries and dependency direction

These are referenced directly rather than published to a feed. Reference what you need; each brings
the core with it.

```text
                 ArxisStudio.ProjectSystem              provider-neutral core
                      ↑                ↑
                      │                │
ArxisStudio.ProjectSystem.MSBuild      ArxisStudio.ProjectSystem.NuGet
  reads projects                         writes them

Later, optional:
ArxisStudio.ProjectSystem.Markup.Xaml     adapter onto ArxisStudio.Markup (Milestone 7)
```

Arrows mean "depends on". The core depends on nothing but the base class library. A tool that only
wants the model — to cache it, to render it, to write its own provider — references the core alone
and never loads MSBuild.

**Each package hosts exactly one engine.** MSBuild reads projects and NuGet writes them; neither
references the other, and neither references the other's engine. A package manager that could
evaluate would start to, and then two packages would read project files and eventually disagree
about one. That is enforced per package, not as a special case for the core.

### What the core is independent of, and why it matters

The core references **no** MSBuild, NuGet, Roslyn, Avalonia, `ArxisStudio.Markup`, or UI-framework
assembly, and no such type appears anywhere in its public API. Architecture tests enforce this
mechanically, against both the project file and the compiled assembly.

This is not tidiness. Provider-owned objects have global state, caches, load contexts, lifetime
rules, and sometimes process-isolation requirements. A model built out of them stops being
readable the moment the provider releases them. Because nothing provider-shaped crosses the
boundary, a snapshot stays valid and inspectable after the machinery that produced it is gone —
and a future provider can run in a worker process without any consumer-facing type changing.

Integration with `ArxisStudio.Markup` is deliberately *not* a dependency in either direction. It
belongs in a separate adapter package that depends on both.

## Core concepts

| Concept | What it is |
| --- | --- |
| Identity | Strongly typed, value-equal handles for a workspace, solution, and project |
| Canonical path | One normalized absolute path type with one documented comparison policy |
| Entry point | What the user asked to open — a solution, a solution XML file, or a project |
| Snapshot | An immutable, complete picture of a solution and its projects at one version |
| Reference | Project, package, framework, assembly, and analyzer references as plain data |
| Resolved package | What restore actually chose: exact version, transitive origin, and assemblies |
| Item | A project item: kind, include, optional canonical path, metadata |
| Output artifact | Where a build put something — a path and a kind, never a loaded assembly |
| Diagnostic | A stable code, a severity, and a location, in place of an exception |
| Provider | The asynchronous boundary a format- or engine-specific package implements |
| Workspace | Owns the current snapshot, its version, and the order refreshes happen in |

## Threading and cancellation

The core has **no UI-thread affinity** and does not capture a synchronization context; every
`await` in the shipping assembly is `ConfigureAwait(false)`, enforced by the compiler.

Reading the current snapshot is lock-free and safe from any thread. Loads and refreshes are
serialized through one mutation boundary in **the order the requests arrive**, so a slow older
refresh can never overwrite a newer one because of scheduler order.

Every asynchronous operation accepts a `CancellationToken`. Cancellation surfaces as
`OperationCanceledException` — never as a diagnostic — and a cancelled refresh leaves the previous
snapshot and version exactly as they were.

Two properties of `SnapshotChanged` that a subscriber has to know: a **throwing handler is
isolated**, so the others still run and nothing reports the failure; and **delivery is not ordered**,
because the boundary is released before the event is raised. Publication order is strict; a handler
that keeps derived state compares `e.Version` and ignores anything older.

## Diagnostics, not exceptions

A missing project, an unsupported entry point, a malformed provider result, or a failed
evaluation is an ordinary tool scenario, not an exceptional one. Each is reported as a diagnostic
with a stable machine-readable code and a location. Messages are for people and may be reworded;
codes are the contract.

| Range | Raised by |
| --- | --- |
| `APS1xxx` | Core: requests, paths, snapshots, workspace, invariants |
| `APS2xxx` | MSBuild discovery and evaluation |
| `APS3xxx` | Restore and build execution |
| `APS4xxx` | NuGet package-management operations |
| `APS5xxx` | Integration adapters |

`APS1xxx` through `APS4xxx` have implemented codes; the rest are reserved. A code with no
producer is a promise the library has not made, so none is declared. The ranges divide by concern
rather than by assembly: the MSBuild package raises `APS2xxx` for evaluating a project and `APS3xxx`
for building one, because a consumer routing on "the build failed" should not have to know which
package happened to run it.

A diagnostic may also carry **the engine's own code** — `MSB4011` for a duplicate import,
`NETSDK1147` for a missing workload — when the engine is what noticed the problem. Renaming those
would put the only useful distinction in the message text, which is exactly what routing on `Code`
exists to avoid. `ProviderName` says which engine spoke. Exceptions remain reserved for invalid API use, cancellation, disposed objects, broken
invariants, and unrecoverable failures.

## Security and trust

**The core executes nothing.** It does not load assemblies, evaluate MSBuild, run targets or tasks,
restore packages, execute scripts, or instantiate project code. It has no filesystem access at all —
paths are values it normalizes and compares, never files it opens.

**`ArxisStudio.ProjectSystem.MSBuild` does execute things**, and that difference is much of why they
are separate packages. Evaluating a project runs SDK resolvers, imports and property functions
declared by that project, in this process — see
[ADR 0009](docs/adr/0009-evaluation-happens-in-process.md). Building one goes further still: targets
run tasks, and tasks are arbitrary code the project chose, including whatever a restore downloaded.

**So do not open a project you do not trust.** There is no sandbox here and none is claimed. Doing
that safely needs an explicit trust and process-isolation design, which is why the model was built
so evaluation can move to a worker process without any consumer-facing type changing — see
[ADR 0003](docs/adr/0003-provider-types-do-not-cross-the-boundary.md). Path validation is *not* a
security boundary and is not offered as one.

## Roadmap

| Milestone | Scope |
| --- | --- |
| 0 | Core model, snapshots, diagnostics, provider boundary, workspace |
| 1 | `ArxisStudio.ProjectSystem.MSBuild`: locate and host MSBuild, open standalone projects |
| 2 | Solutions and the project graph: `.sln`, `.slnx`, solution folders, configurations |
| 3 | References and restore assets, `project.assets.json`, SDK and workload diagnostics |
| 4 | Explicit restore and build operations with structured progress |
| 5 | File watching, debouncing, invalidation, incremental refresh |
| **6** | `ArxisStudio.ProjectSystem.NuGet`: install, update, remove — *this one* |
| 7 | `ArxisStudio.ProjectSystem.Markup.Xaml`: optional adapter onto the Markup resolvers |

## Build and test

```bash
dotnet restore
dotnet build -c Release --no-restore -warnaserror
dotnet test -c Release --no-build
```

A single test project:

```bash
dotnet test tests/ArxisStudio.ProjectSystem.Architecture.Tests -c Release
```

The SDK is pinned in `global.json`. Warnings are errors, so a warning is a build failure.

## Known limitations

Recorded honestly in [docs/limitations.md](docs/limitations.md). Read it before promising anything
to your users.

## Contributing

Development proceeds one milestone at a time, and the milestone boundaries are real: work that
belongs to a later one does not land early, and no placeholder API is shipped for a concept that
is not implemented.

Architectural decisions are recorded as ADRs in [docs/adr](docs/adr). A change that departs from a
recorded decision amends or supersedes the ADR in the same commit. Commits follow Conventional
Commits, and each carries one architectural purpose.

Every functional change comes with a test, and a bug fix comes with a failing test first.

## License

MIT — see [LICENSE](LICENSE).

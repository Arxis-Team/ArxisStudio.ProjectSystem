# ArxisStudio.ProjectSystem

Provider-neutral infrastructure for tools that need to understand a .NET solution: what projects
it contains, what they reference, what they target, and where their outputs land — without the
tool taking a dependency on MSBuild, NuGet, Roslyn, or any UI framework.

The audience is IDEs, designers, hot-reload hosts, analyzers, refactoring tools, and
project-management applications. What they share is a need for a *model* of a solution that is
stable, immutable, and safe to read from several threads while something else reloads it.

## Status

**Milestone 0 — complete.** This is the foundation: the model, the diagnostics, the provider
boundary, and the workspace that publishes snapshots. It ships one package and no file-format
provider at all.

That last point is the important one. **This package does not read `.sln`, `.slnx`, or `.csproj`
files.** It models those concepts so that a provider can populate them, and the first provider —
`ArxisStudio.ProjectSystem.MSBuild` — is Milestone 1. Until it exists, the only way to get data
into a workspace is to write a provider yourself.

## A minimal example

There is no MSBuild provider yet, so this one invents a project. Writing a provider like it is also
how a tool built on this package gets tested without an MSBuild workload on the machine.

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

## Packages and dependency direction

```text
ArxisStudio.ProjectSystem                 provider-neutral core — this package
        ↑
ArxisStudio.ProjectSystem.MSBuild         discovery and evaluation (Milestone 1)

Later, optional:
ArxisStudio.ProjectSystem.NuGet           package-management operations (Milestone 6)
ArxisStudio.ProjectSystem.Markup.Xaml     adapter onto ArxisStudio.Markup (Milestone 7)
```

Arrows mean "depends on". The core depends on nothing but the base class library.

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

Only the core range has implemented codes; a code with no producer is a promise the library has
not made. Exceptions remain reserved for invalid API use, cancellation, disposed objects, broken
invariants, and unrecoverable failures.

## Security and trust

**This package executes nothing.** It does not load assemblies, evaluate MSBuild, run targets or
tasks, restore packages, execute scripts, or instantiate project code. It has no filesystem access
at all — paths are values it normalizes and compares, never files it opens.

Future MSBuild evaluation and build operations are a different matter: they can execute SDK
resolvers, imports, tasks, targets, and tools declared by the project being opened. Opening an
untrusted project will therefore require an explicit trust and process-isolation design before
those operations are implemented. Path validation in this package is *not* a security boundary and
is not offered as one.

## Roadmap

| Milestone | Scope |
| --- | --- |
| **0** | Core model, snapshots, diagnostics, provider boundary, workspace — *this one* |
| 1 | `ArxisStudio.ProjectSystem.MSBuild`: locate and host MSBuild, open standalone projects |
| 2 | Solutions and the project graph: `.sln`, `.slnx`, solution folders, configurations |
| 3 | References and restore assets, `project.assets.json`, SDK and workload diagnostics |
| 4 | Explicit restore and build operations with structured progress |
| 5 | File watching, debouncing, invalidation, incremental refresh |
| 6 | `ArxisStudio.ProjectSystem.NuGet`: search, install, update, remove |
| 7 | `ArxisStudio.ProjectSystem.Markup.Xaml`: optional adapter onto the Markup resolvers |

## Build and test

```bash
dotnet restore
dotnet build -c Release --no-restore -warnaserror
dotnet test -c Release --no-build
dotnet pack -c Release --no-build -o artifacts
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

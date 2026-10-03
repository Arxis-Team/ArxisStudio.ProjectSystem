# 35. An operation runs in a worker node

Date: 2026-10-04
Status: Accepted

## Context

Restore, build, rebuild and clean go through `BuildManager.DefaultBuildManager`
([ADR 0014](0014-an-operation-is-not-a-mutation.md)). With default parameters the build manager
keeps an in-process node and prefers it, so every target of an operation ran in the host — the same
process that evaluates ([ADR 0009](0009-evaluation-happens-in-process.md)) and that, in an IDE, also
hosts everything else.

Running targets is not evaluating. Evaluation runs SDK resolvers and property functions; targets run
**tasks**, and tasks load assemblies of their own. The SDK's tasks load its copies of NuGet,
`System.Text.Json` and their kin, and MSBuild's task loader puts an assembly that sits beside
`MSBuild.dll` into the **default** load context by path, so the copy is shared with other tasks.

That collides with a host that carries the same assembly. ArxisStudio's project service manages
packages, so its default context holds its own `NuGet.Versioning`. The first SDK-style build in the
IDE reached `ResolvePackageAssets`, the task asked for the SDK's `NuGet.Versioning` — the same
assembly version, another file — and the runtime refused a second assembly of that name in one
context:

```
MSB4018: The "ResolvePackageAssets" task failed unexpectedly.
System.IO.FileLoadException: Could not load file or assembly 'NuGet.Versioning, Version=7.9.0.0 …'.
Assembly with same name is already loaded
```

Every design build of every SDK-style project failed this way, and nothing in this repository saw it:
the operation tests build a plain MSBuild project whose targets load nothing, in a test process that
carries no NuGet of its own.

## Decision

An operation's targets run in a **worker node**: `MSBuildOperationRunner` sets
`BuildParameters.DisableInProcNode`, and the build manager starts or reuses an out-of-process
MSBuild node (`dotnet MSBuild.dll /nodemode:1`) of the SDK the host registered, and hands it the
request.

- A node loads what the build loads and nothing of the host's, so no host can collide with a task.
- A task that crashes, hangs on its own or leaks takes the node with it, not the host. Cancellation
  is unchanged: `CancelAllSubmissions` reaches the node.
- Node reuse stays the build manager's default. A node outlives the build that started it and serves
  the next, so a designer's build after every save does not pay a node start each time; an idle node
  exits on its own, exactly as one a `dotnet build` leaves behind.

Evaluation stays where it is. It runs no tasks, the model's translation needs the evaluated project
in this process, and ADR 0009's reasons for keeping it here are unchanged.

`AnOperation_RunsItsTargetsOutsideThisProcess` holds the decision: the fixture reports the command
line of the process that expands a target's property functions — the node that runs the target — and
the test requires a node's.

## Consequences

- **The first operation of a session may pay for a node start** — a process and an SDK warming
  up — unless a node of the same SDK is already idle; the following ones reuse it.
- **A build's environment is the host's at the time it asks.** The node takes the requester's
  environment with each request, so a variable the host sets after starting a node still reaches the
  build.
- **Nodes are shared with command-line builds of the same SDK**, because the handshake is the SDK's
  and not the host's — the same sharing two terminals already have.
- A host that wants targets in its own process for a reason of its own has no switch for it. None is
  offered: the reason would have to outweigh every host that carries an assembly a task also loads,
  and that is every IDE.

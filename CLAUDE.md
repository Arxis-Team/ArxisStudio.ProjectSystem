# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## The contract

`ARXISSTUDIO_PROJECTSYSTEM_INITIAL_TASK.md` (supplied with the work, currently at
`C:\Users\Maxim\Downloads\`) is the **architectural contract** for Milestone 0. `README.md` is the
public statement of what the package is. Where they disagree, the task specification wins and the
README is what gets corrected.

Development proceeds one milestone at a time. Do not start Milestone 1 — the MSBuild provider —
while Milestone 0 is open, and do not ship placeholder API for a concept a later milestone owns.

## Build and test

```bash
dotnet restore
dotnet build -c Release -warnaserror
dotnet test -c Release
dotnet pack -c Release -o artifacts        # one package from src/

# one test project
dotnet test tests/ArxisStudio.ProjectSystem.Architecture.Tests -c Release

# one test by name (xunit.v3)
dotnet test tests/ArxisStudio.ProjectSystem.Tests -c Release --filter 'FullyQualifiedName~CanonicalPathTests'
```

`global.json` pins SDK `10.0.101` with `rollForward: latestFeature`; the installed SDK is
`10.0.301` and satisfies it. Always invoke `dotnet` from the repository root.

The solution is `ArxisStudio.ProjectSystem.slnx` — the XML format, which this SDK creates by
default. There is no `.sln`.

## Package boundaries

```text
ArxisStudio.ProjectSystem                 provider-neutral core — the only package that ships now
        ↑
ArxisStudio.ProjectSystem.MSBuild         Milestone 1
ArxisStudio.ProjectSystem.NuGet           Milestone 6
ArxisStudio.ProjectSystem.Markup.Xaml     Milestone 7, depends on ProjectSystem *and* Markup
```

**The core references nothing.** Not MSBuild, not NuGet, not Roslyn, not Avalonia, not
`ArxisStudio.Markup`, not any UI framework, not any IDE API — and no such type may appear in its
public surface. `tests/ArxisStudio.ProjectSystem.Architecture.Tests` enforces this against both
the project file and the compiled assembly. **If one of those tests fails, the code is in the
wrong package. Move it; never relax the test.**

Note the direction carefully, because it is the opposite of what Markup's own README suggests:
integration with `ArxisStudio.Markup` is **not** a dependency of this core. It belongs in a
separate adapter package that depends on both, and neither core may depend on that adapter.

## The rules that shape every design decision

### Snapshots are immutable, and publication is one reference write

All published workspace state lives in one immutable object. A refresh builds a complete candidate
and replaces one reference; readers take the current state with `Volatile.Read` and no lock, so a
reader observes the state wholly before or wholly after a change and never a torn view.

Never mutate a snapshot after publication. Never publish a half-built provider result. A failed or
cancelled refresh must not advance the version and must leave the previous snapshot in place.

Collections on a snapshot are empty, never `null`, and never `default(ImmutableArray<T>)` — a
default immutable array throws on enumeration and looks exactly like the library returning null
after promising it would not.

### Provider implementation details do not escape

`ProjectInstance`, `ProjectCollection`, MSBuild items, NuGet lock-file types, Roslyn workspaces
and their kin must never cross the boundary. Provider-owned objects have global state, caches,
load contexts and lifetimes; a model built from them stops being readable once the provider lets
go. Everything a provider returns is defensively materialized into core-owned immutable state.

The test of this rule: a snapshot must stay valid and inspectable after every provider-specific
object has been released, and a future provider must be able to run out-of-process without any
consumer-facing type changing.

### Ordinary project problems are diagnostics

A missing project, an unsupported entry point, a malformed provider result, a failed evaluation —
these are expected tool scenarios and are reported as diagnostics with stable codes, never as
implementation exceptions. Reserve exceptions for invalid API arguments, cancellation, disposed
objects, broken invariants, and unrecoverable failures.

A provider that throws is caught and turned into a diagnostic. The exception never reaches the
public contract.

Code ranges: `APS1xxx` core, `APS2xxx` MSBuild, `APS3xxx` restore/build, `APS4xxx` NuGet,
`APS5xxx` adapters. **Declare no code nothing raises** — a code with no producer is a promise the
library has not made.

Cancellation is `OperationCanceledException`, never a diagnostic.

### Async and cancellation are first-class

Every operation that may reach a provider, read external state, or wait for the mutation boundary
is asynchronous and takes a `CancellationToken`.

No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()`, no fire-and-forget, no sync-over-async.
The core has no UI-thread affinity and must not capture a synchronization context: every `await`
in `src/` is `ConfigureAwait(false)`, and `.editorconfig` makes `CA2007` an error there so the
compiler enforces it rather than review.

### The workspace owns versions and order

Every successful publication advances a monotonically increasing version. Failed and cancelled
refreshes do not.

Loads and refreshes take one mutation boundary **in the order the requests arrive**. This is a
FIFO gate, not a `SemaphoreSlim`: a semaphore gives mutual exclusion and says nothing about
release order, and an older refresh released last would leave the workspace holding a snapshot
from two refreshes ago with every mutation perfectly serialized. There is no coalescing.

Three things the sibling repository learned the hard way, recorded in
`ArxisStudio.Markup/docs/adr/0011-what-a-review-found-in-the-mutation-boundary.md` and not to be
rediscovered here: state and disposal checks happen **inside** the gate and the answer read on the
way in is not consulted; the disposal flag is an `Interlocked` int, not a `bool`, because disposal
and the operations it races are on different threads by design; and events are raised after
publication and **outside** the gate, because a subscriber is arbitrary user code.

## Public API discipline

`Microsoft.CodeAnalysis.PublicApiAnalyzers` is active and `RS0016`/`RS0017` are errors. New public
API must be declared in `src/ArxisStudio.ProjectSystem/PublicAPI.Unshipped.txt` or the build fails.

There is no sync script in this repository. Populate the file from the build's own `RS0016`
diagnostics: the quoted signature in each is language-neutral, which matters because the CLI here
is Russian-localized. Review the resulting diff deliberately — it is the reviewable summary of
what a change added to the public surface.

`PublicAPI.Shipped.txt` stays empty until a release is intentionally being prepared. Moving
entries into it is a separate, deliberate act.

Every public member needs XML documentation; `CS1591` is a warning and warnings are errors.

## Test determinism

Tests must not depend on an installed IDE, MSBuild workload, NuGet cache, network, or
machine-specific path.

**No `Thread.Sleep`, no `Task.Delay`, no probabilistic stress loops** as substitutes for
coordination. Concurrency is tested with controllable fake providers that park inside an operation
on a `TaskCompletionSource` and resume when the test says so, which is what makes the assertions
exact rather than likely.

Test names are `Method_Scenario_Expectation`; `CA1707` is switched off under `tests/` for exactly
that reason.

## ADRs

Architectural decisions live in `docs/adr/`, numbered, using the template of the existing files:
title, Date, Status, Context, Decision, Consequences.

Record a decision when it constrains future work or when the obvious alternative was rejected for
a reason worth remembering. A change that departs from a recorded decision amends or supersedes
that ADR **in the same commit**. Report a required architectural deviation before implementing it,
not after.

## Conventions

- `.editorconfig`: 4 spaces, LF, file-scoped namespaces, `System.*` usings first.
- Documentation, XML docs, comments and identifiers are in **English**.
- Commits follow **Conventional Commits** (`feat(workspace): …`, `test(arch): …`), one
  architectural purpose each.
- Central package management: every version lives in `Directory.Packages.props`, and
  `PackageReference` elements carry no `Version`.
- Prefer immutable public models. No global mutable state, no service locators.
- Add a test with every functional change; add a failing test before fixing a bug.

## Preserve unrelated changes

The working tree may contain edits that are not yours. Do not revert, reformat, or "tidy" code a
task did not ask you to touch, and do not commit unrelated changes alongside your own. If a file
you must edit already has uncommitted modifications, work with them rather than over them.

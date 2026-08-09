# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## The contract

`ARXISSTUDIO_PROJECTSYSTEM_INITIAL_TASK.md` (supplied with the work, currently at
`C:\Users\Maxim\Downloads\`) is the **architectural contract**. Its Milestone 0 is complete and its
roadmap defines what follows. `README.md` is the public statement of what the packages are. Where
they disagree, the task specification wins and the README is what gets corrected.

Development proceeds one milestone at a time. **All seven milestones are complete.** Further work
extends what is there rather than filling in a roadmap, so the discipline that matters now is the
one that always did: do not ship API for a concept nothing implements, and record a decision that
constrains future work. `docs/limitations.md` is the honest list of what was deliberately left
undone and why — read it before adding something it already argues against.

## Build and test

```bash
dotnet restore
dotnet build -c Release -warnaserror
dotnet test -c Release

# one test project
dotnet test tests/ArxisStudio.ProjectSystem.Architecture.Tests -c Release

# one test by name (xunit.v3)
dotnet test tests/ArxisStudio.ProjectSystem.Tests -c Release --filter 'FullyQualifiedName~CanonicalPathTests'
```

The MSBuild provider brings one rule the rest of the repository does not have. `MSBuildLocator` must
run before any `Microsoft.Build` type is loaded, and the runtime loads an assembly when a method
*naming* its types is first entered — not when the line executes. So those names live in
`MSBuildProjectEvaluator` and `MSBuildOperationRunner` alone, `MSBuildEnvironment` names only the
locator's own, and `MSBuildProjectProvider` names none of them. Adding a
`catch (InvalidProjectFileException)` to the provider would break it, silently, somewhere unrelated.
That is why the evaluator has a `Try` shape rather than throwing, and why both engine-facing types
are `[MethodImpl(MethodImplOptions.NoInlining)]`.

`global.json` pins SDK `10.0.101` with `rollForward: latestFeature`; the installed SDK is
`10.0.301` and satisfies it. Always invoke `dotnet` from the repository root.

The solution is `ArxisStudio.ProjectSystem.sln`, the classic format — a deliberate deviation from
the task specification, which asks for `.slnx`. See
[ADR 0004](docs/adr/0004-the-solution-file-is-the-classic-format.md). It is the one file in the
repository exempt from `eol=lf`, because the IDEs rewrite it with a BOM and CRLF whenever they
touch it.

## Package boundaries

```text
ArxisStudio.ProjectSystem                 provider-neutral core
        ↑            ↑            ↑
        │            │            └── ArxisStudio.ProjectSystem.Markup.Xaml
        │            │                    (also depends on ArxisStudio.Markup, in the sibling repo)
        │            └── ArxisStudio.ProjectSystem.NuGet    package management
        │
ArxisStudio.ProjectSystem.MSBuild         MSBuild discovery and evaluation
```

Note that these sit **beside** each other rather than stacking. Each package hosts exactly one
engine: MSBuild reads projects, NuGet writes them, the adapter speaks Markup, and none references
another's. A package manager that could evaluate would start to, and then two packages would read
project files and eventually disagree.

The adapter is the one package allowed near `ArxisStudio.Markup` and `Avalonia`, and the one whose
public surface may expose them — see
[ADR 0018](docs/adr/0018-the-adapter-references-markup-by-source.md). It references Markup by a
relative `ProjectReference` into the sibling repository, so **the two repositories must sit beside
each other**; the adapter's project file fails with a sentence saying so if they do not. Markup's own
projects must not be added to this solution — `dotnet sln add` will pull them in, and they should be
taken back out.

Two questions hide behind "what may a package depend on", and they have different answers. **What a
package may reference** depends on the package: the MSBuild provider exists to host MSBuild, and the
package manager to host NuGet. **What may appear in a public surface** does not: no consumer of any
package in this family should need MSBuild or NuGet on their compile line to read a snapshot.
`ForbiddenDependencies` has a method for each and they are not interchangeable.

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

The ranges divide by **concern, not by assembly**: the MSBuild package holds two catalogues,
`MSBuildDiagnosticCodes` for evaluating a project and `OperationDiagnosticCodes` for building one,
because a consumer routing on "the build failed" should not have to know which package ran it. A
condition that means the same thing in both keeps one code — a missing project file is `APS2003`
whether it was going to be evaluated or built. `DiagnosticCatalogueTests` enforces the ranges per
catalogue, so a new one must be listed there with the range it owns.

A diagnostic may also carry an engine's own code — `MSB4011`, `NETSDK1147` — when the engine is what
noticed. The split is by who noticed, not by who reported, and [ADR 0013](docs/adr/0013-a-provider-may-keep-its-engines-diagnostic-codes.md)
says why renaming those would break the rule it appears to serve.

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

**Restore and build are not mutations and do not take the gate.** They change what is on disk, not
what the workspace knows, so they publish nothing, do not advance the version, and `DisposeAsync`
does not wait for them — [ADR 0014](docs/adr/0014-an-operation-is-not-a-mutation.md). Anything that
re-reads a project belongs on the load path instead, behind the gate, where it can publish.

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

What is recorded so far:

| ADR | Decision |
| --- | --- |
| [0001](docs/adr/0001-core-is-provider-neutral.md) | The core references nothing; engines enter through a provider |
| [0002](docs/adr/0002-published-state-is-immutable-snapshots.md) | Published state is immutable, and publication is one reference write |
| [0003](docs/adr/0003-provider-types-do-not-cross-the-boundary.md) | Nothing process-bound reaches the model, which is what permits an out-of-process provider |
| [0004](docs/adr/0004-the-solution-file-is-the-classic-format.md) | `.sln` rather than the specification's `.slnx`, for consistency with the siblings |
| [0005](docs/adr/0005-one-path-policy-and-it-is-case-insensitive.md) | One path policy, case-insensitive on every platform, because MSBuild's is |
| [0006](docs/adr/0006-one-fifo-mutation-boundary.md) | A FIFO queue rather than a `SemaphoreSlim`, which promises no ordering |
| [0007](docs/adr/0007-a-throwing-subscriber-is-isolated.md) | A throwing subscriber is isolated; delivery order is not a promise |
| [0008](docs/adr/0008-a-result-state-computed-not-declared.md) | `Status` is computed, so it cannot disagree with the evidence |
| [0009](docs/adr/0009-evaluation-happens-in-process.md) | Evaluation runs in this process; the seam a worker needs is kept clean |
| [0010](docs/adr/0010-testing-a-provider-that-needs-a-real-engine.md) | The translation is tested without MSBuild; the wiring, sparingly, with it |
| [0011](docs/adr/0011-these-libraries-are-referenced-not-published.md) | Referenced directly, not published, so the packaging apparatus is gone |
| [0012](docs/adr/0012-restore-assets-are-read-not-resolved.md) | `project.assets.json` is read by hand, so no NuGet client library enters |
| [0013](docs/adr/0013-a-provider-may-keep-its-engines-diagnostic-codes.md) | A provider passes its engine's codes through rather than renaming them |
| [0014](docs/adr/0014-an-operation-is-not-a-mutation.md) | Building changes disk, not the model, so it stays outside the mutation boundary |
| [0015](docs/adr/0015-invalidation-is-not-transitive.md) | Evaluating a project does not read its references, so staleness does not spread |
| [0016](docs/adr/0016-watching-belongs-with-the-provider.md) | Watching is composed by the host from four pieces; the workspace is not one of them |
| [0017](docs/adr/0017-project-files-are-edited-as-xml.md) | Project files are edited as XML; each package hosts one engine and no other |
| [0018](docs/adr/0018-the-adapter-references-markup-by-source.md) | The adapter references Markup by source, and is the one package allowed to |
| [0019](docs/adr/0019-a-file-that-is-not-there-yet-is-an-evaluation-input.md) | A provider names where a convention-based import would be, not only where one was found |
| [0020](docs/adr/0020-the-adapter-resets-avalonias-runtime-xaml-compiler.md) | The adapter resets Avalonias runtime XAML compiler between generations |

0004 and 0011 are deviations from the task specification. The rest record decisions the specification
left open, or alternatives rejected for reasons worth not rediscovering.

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

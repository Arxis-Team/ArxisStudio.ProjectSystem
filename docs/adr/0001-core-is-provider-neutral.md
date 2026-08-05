# 1. The core is provider-neutral

Date: 2026-08-05
Status: Accepted

## Context

`ArxisStudio.ProjectSystem` exists to give tools a model of a .NET solution. The obvious way to
build one is to reference MSBuild, evaluate projects, and expose the results — which is what most
project systems do, and it is what the consumers of this package must never be forced into.

The consumers are IDEs, designers, hot-reload hosts, analyzers, refactoring tools, and
project-management applications. Some of them will want a solution model without a build engine in
their process at all; some already have their own Roslyn workspace and cannot afford a second
opinion about which MSBuild to load; some will eventually want the evaluation to happen in a worker
process that can be killed when it wedges.

There is a second reason, and it is the one that cost the sibling repository the most: MSBuild,
NuGet and Roslyn all carry global state — locations, caches, toolsets, load contexts — that a
library which touches them inherits and imposes on everyone who references it.

## Decision

The core references **nothing** but the base class library. Specifically no Microsoft.Build, no
NuGet client, no Roslyn, no Avalonia, no `ArxisStudio.Markup`, no UI framework and no
platform-specific IDE API, and no such type appears anywhere in its public surface.

Everything engine-specific enters through `IProjectSystemProvider`, which a separate package
implements. `ArxisStudio.ProjectSystem.MSBuild` is the first of those and is Milestone 1.

Integration with `ArxisStudio.Markup` is deliberately not a dependency in either direction. It
belongs in a `ArxisStudio.ProjectSystem.Markup.Xaml` adapter that depends on both. This is worth
stating explicitly because Markup's own README reads as though the dependency runs the other way,
and the first draft of this repository's `CLAUDE.md` believed it.

The rule is enforced mechanically by `tests/ArxisStudio.ProjectSystem.Architecture.Tests`, against
both the declared project graph and the compiled assembly's references, and each guard has been
mutation-tested — the forbidden reference was added, the test was watched to fail, and it was
removed again.

## Consequences

- The core ships with an empty dependency group. A consumer restoring it gets one assembly.
- A consumer can model, cache and inspect a solution without a build engine in the process, and a
  consumer that already hosts MSBuild does not get a second copy.
- The core cannot answer any question that needs evaluation. This milestone therefore ships a model
  and no data to put in it, which is the honest cost and is stated at the top of the README rather
  than discovered.
- A provider is free to be as stateful, as global and as process-bound as its engine requires,
  because none of that reaches the model it produces.
- If a rule ever has to be relaxed, the architecture test is what will say so first. The fix is to
  move the code into a provider package; the test is not to be relaxed.

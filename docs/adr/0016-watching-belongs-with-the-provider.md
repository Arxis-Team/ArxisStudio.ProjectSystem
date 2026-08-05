# 16. Watching belongs with the provider, and the workspace does not do it

Date: 2026-08-05
Status: Accepted

## Context

Milestone 5 asks for file-change observation. Two questions had to be answered, and the second is
the one that shapes the design.

**Where can a watcher live?** Not the core. `ArxisStudio.ProjectSystem` promises it has no
filesystem access at all, and `CoreSourceRulesTests` enforces it by reading the source. So the three
candidates were the MSBuild provider, a new package of its own, or relaxing the rule.

**Should the workspace watch by itself?** The obvious feature is a workspace that notices a change
and refreshes without being asked. It is also the one that would have to reach a provider, take the
mutation boundary, and start an asynchronous refresh from a timer callback — with no caller to hand
the resulting `Task` to. That is fire-and-forget, which the contract forbids outright, and the
forbidding is not arbitrary: a refresh that fails would have nowhere to report to.

## Decision

**Watching is composed by the host from four pieces, none of which is the workspace.**

`ProjectSnapshot.EvaluationInputs` says what to watch. `ProjectFileWatcher` observes it.
`FileChangeCoalescer` batches what it reports. `SolutionSnapshot.Invalidate` decides whether the
batch matters. The host calls `RefreshAsync` when it does — with a caller, a token, and somewhere
for a failure to go.

Each piece is separately useful and separately testable, and three of the four are pure. A host that
already observes files — an IDE almost always does — uses its own and skips the second piece
entirely, which is why the coalescer takes bare paths rather than a watcher's event type.

**The watcher lives in `ArxisStudio.ProjectSystem.MSBuild`,** not in a package of its own. The
mechanism is provider-neutral, but doing it correctly is not: knowing that a missing `obj` must be
watched through its parent, or that a project's imports are the interesting files and the SDK's are
not, is knowledge about how *this* provider evaluates. A neutral watching package would either lack
that and be wrong, or contain it and not be neutral. Since
[ADR 0011](0011-these-libraries-are-referenced-not-published.md) means a package boundary buys no
distribution benefit here, one that buys no conceptual clarity either is not worth its cost.

## Consequences

- **Nothing refreshes on its own.** A host that wants automatic refresh writes the five lines that
  connect the pieces, and owns the decision of when a refresh is appropriate — which it is better
  placed to make anyway, since it knows whether the user is mid-edit or mid-debug.
- **The core stays free of the filesystem and of timers it did not ask for.** The coalescer is in
  the core because it is pure policy over an injected `TimeProvider`; the watcher is not, because it
  is not.
- **A host with its own file observation ignores half of this.** That is the expected case rather
  than a fallback, and the coalescer's signature is what makes it painless.
- **The watcher reports files nobody asked about.** It watches directories, because a watcher bound
  to a single file goes deaf when that file is replaced rather than edited — which is what atomic
  saves do — and can never see it appear. Filtering is `Invalidate`'s job, and the separation is
  what keeps the watcher small enough to trust.
- **A buffer overflow reports everything watched.** The operating system's notification buffer is
  finite and a branch switch exhausts it; when that happens the lost changes are unknowable, so the
  only answer that cannot silently miss one is to assume they all changed.
- If a future provider evaluates out of process, its watching goes with it, because the two are the
  same knowledge.

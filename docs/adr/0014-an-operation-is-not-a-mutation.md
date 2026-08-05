# 14. An operation is not a mutation

Date: 2026-08-05
Status: Accepted

## Context

Milestone 4 adds restore, build, rebuild and clean. They arrive at the same object every other
request does — `ProjectWorkspace` — and the workspace already owns a strict discipline for requests:
one FIFO mutation boundary, a monotonic version, and one immutable snapshot published per success
([ADR 0002](0002-published-state-is-immutable-snapshots.md),
[ADR 0006](0006-one-fifo-mutation-boundary.md)).

The obvious move is to run an operation through that discipline too. It is a long-running thing that
touches the project, so it looks like the same kind of request as a load, and treating it as one
would need no new thinking.

It is not the same kind of request, and the difference is worth stating precisely: **a load changes
what the workspace knows; an operation changes what is on disk.** A build writes assemblies. It does
not re-read a single project file, so afterwards the workspace knows exactly what it knew before.

That distinction decides three questions at once.

## Decision

An operation is a separate capability, on a separate interface, outside the mutation boundary, and
it publishes nothing.

**Separate interface.** `IProjectOperationProvider` is optional and independent of
`IProjectSystemProvider`. A provider that reads projects need not build them — a future
out-of-process or read-only provider is a real case, and a mandatory `ExecuteAsync` would force it
to declare a capability it does not have. A workspace routes by asking `CanExecute`, and no provider
that can do it is `APS1004`, an ordinary diagnostic rather than a broken configuration.

**Outside the gate.** Builds are slow — minutes, not milliseconds. Taking the mutation boundary for
one would stall every load and refresh behind it, and a designer that cannot re-read a XAML file
while a build runs is a designer that freezes for the length of the build. Nothing requires the
exclusion, either: the operation publishes no state, so there is nothing for it to race with. It
does not advance the version, and `DisposeAsync` does not wait for it.

**Publishes nothing.** A snapshot published after a build would claim the model had changed when
nothing had been re-read. If a caller wants the model to reflect what the build produced, it calls
`RefreshAsync` — explicitly, at a moment it chose. That keeps the rule that a version increment
always means "something was re-read", which is the only reason a version is worth comparing.

## Consequences

- **A build's outcome is a return value, not workspace state.** `ProjectOperationResult` is handed
  back to the caller and stored nowhere. Two callers building concurrently each get their own
  answer, and neither overwrites the other's.
- **The result cannot disagree with its own evidence,** the same discipline as `WorkspaceLoadResult`
  ([ADR 0008](0008-a-result-state-computed-not-declared.md)): `Succeeded` with an error diagnostic
  throws, and so does `Failed` without one.
- **A caller must refresh to see what a build produced.** This is a real cost and the honest one to
  pay: the alternative was a version that sometimes means "re-read" and sometimes means "rebuilt",
  which no consumer could act on.
- **Disposal does not wait for a running operation.** A workspace disposed mid-build returns at once
  and the build finishes on its own. The alternative — blocking disposal for minutes — is worse, and
  the operation has no workspace state to corrupt on its way out.
- **One build at a time, per process.** `BuildManager.DefaultBuildManager` is a singleton, so the
  MSBuild provider serialises builds behind a lock of its own. That is a provider's constraint, not
  the workspace's; the boundary above it stays free.
- **Cancellation is the caller's token throughout, and never a diagnostic.** A cancelled build
  throws `OperationCanceledException`; it never returns a failed result that a caller would have to
  inspect to tell "you stopped it" from "it broke".

# 3. Provider types do not cross the boundary

Date: 2026-08-05
Status: Accepted

## Context

[ADR 0001](0001-core-is-provider-neutral.md) keeps engine packages out of the core's *references*.
That is not the same as keeping engine *objects* out of the model, and the second is the harder and
more valuable rule.

The tempting design is a snapshot that carries the `ProjectInstance` it was built from, so that a
consumer needing something the model does not expose can reach through. It is one property, it costs
nothing to add, and it destroys every other guarantee here:

- MSBuild's `ProjectInstance` belongs to a `ProjectCollection` with its own lifetime, caches and
  toolset. Once the provider releases it, a snapshot holding one is a snapshot of nothing.
- NuGet lock-file types and Roslyn workspaces come with the same problem in different shapes.
- Anything a consumer can reach through, a consumer eventually does — and the property becomes part
  of the contract, whatever the documentation says about it.
- Worst: a model containing a live engine object can only be read in the process that built it.

## Decision

Everything a provider returns is **defensively materialised into core-owned immutable state** before
it is published, and nothing that could hold a process-bound resource may appear in the model. No
`object`, no `Type`, no delegate, no `Stream`, no `IDisposable`, no `Task`, no array, no mutable
collection.

Defensive copying happens **once, at the type boundary**, not repeatedly at every hand-off. The
builders own ordinary mutable lists and copy them into immutable arrays on the way out, which is why
the workspace does not need to copy the snapshot again: a builder was the only way to make one, so it
is already core-owned.

The workspace still validates what it is handed, because the shapes that would silently corrupt
identity determinism are worth refusing rather than trusting: a snapshot stamped with another
workspace's identity, an entry point disagreeing with the request, duplicate project identities. Each
becomes `APS1003`.

`PublicSurfaceTests.TheSnapshotModel_CarriesOnlyData` walks the model transitively from
`WorkspaceLoadResult`, `SolutionSnapshot` and `ProjectSnapshot` and refuses those shapes. It has been
mutation-tested: an `object` property was added to `ProjectSnapshot`, the test was watched to fail,
and it was removed.

## Consequences

- **A snapshot stays valid and inspectable after every provider-specific object has been released.**
  That is the concrete form of this rule and the thing to check a change against.
- **A future provider can run out-of-process without any consumer-facing type changing.** Nothing in
  the model needs a live object graph, so moving the evaluation across a process boundary is a change
  of transport rather than a change of model. The test above is what turns that from a hope into a
  checked claim.
- A consumer that needs something the model does not carry cannot reach around it. That is the point,
  and the answer is to extend the model deliberately rather than to widen a hole nobody can later
  close.
- `ProjectMetadata` is the deliberate escape hatch: provider-specific evaluated data goes in a
  read-only string map, which crosses a process boundary intact and commits the core to nothing.
- Provider-owned objects may be as stateful and as short-lived as their engine requires, because
  their lifetime ends at the boundary.

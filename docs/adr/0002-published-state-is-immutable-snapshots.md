# 2. Published state is immutable snapshots

Date: 2026-08-05
Status: Accepted

## Context

A tool reads a solution model on whatever thread it happens to be on, often for a long time — a
project tree enumerates every item to build its nodes, an analyzer walks every reference. Meanwhile
a refresh may be rebuilding the whole thing.

The conventional answers are all bad here. A lock around the model makes every reader contend with
every refresh and invites the deadlock where a reader calls back into the workspace. A mutable model
with change notifications gives readers `InvalidOperationException` mid-enumeration, or worse, a
half-updated graph that looks consistent. Copy-on-read costs a copy per reader.

## Decision

All published state lives in one immutable object graph. A refresh builds a **complete candidate**
and publication replaces **one reference**; readers take the current one with `Volatile.Read` and no
lock at all.

Three rules follow, and each is tested:

- **Nothing is mutated after publication.** `SolutionSnapshot` and `ProjectSnapshot` have no public
  constructor and no settable property; a `ProjectSnapshotBuilder` is the only way to make one, and
  it copies everything it was given on the way out.
- **Collections are empty, never `null`, and never `default(ImmutableArray<T>)`.** A default
  immutable array throws on enumeration and looks to a consumer exactly like this library returning
  null after promising it would not. A reflective test walks every collection property of every
  model type, so one added in a later milestone is covered without anyone remembering.
- **A failed or cancelled refresh publishes nothing.** The version does not advance and the previous
  snapshot stays in place, which is what makes an unchanged version mean "nothing changed" rather
  than "nobody looked".

Aggregates — the two snapshot types — keep **reference equality**. The leaves — items, references,
artifacts, diagnostics, metadata — get **value equality**.

## Consequences

- A reader observes the workspace either wholly before or wholly after a change. There is no torn
  view and no lock to take, which also means no lock for a reader to deadlock on.
- A snapshot a consumer captured stays valid and enumerable for as long as it holds it, including
  after the workspace has moved on or been disposed. That is what makes it a snapshot.
- Refreshing rebuilds rather than patches. For this milestone that is the right trade — incremental
  invalidation needs the file-change graph that Milestone 5 introduces, and building it now would
  mean designing against no real trigger.
- The equality split is deliberate and worth remembering: a generated structural `Equals` on a
  snapshot would walk tens of thousands of items, and so would its hash, which makes accidentally
  using one as a dictionary key catastrophic rather than merely slow. Staleness is checked with
  `ProjectIdentity` plus `WorkspaceVersion`, which is `O(1)`.
- `SolutionSnapshot.Version` is carried on the snapshot, not only on the workspace, so that a
  consumer can read a snapshot and its version as one value that cannot disagree. Reading
  `CurrentSnapshot` and `CurrentVersion` separately is two reads a publication can happen between.

# 8. A result state computed, not declared

Date: 2026-08-05
Status: Accepted

## Context

The specification is unusually specific here: "Do not use `Success` in a way that can disagree with
the presence of an error diagnostic or a snapshot. Derive convenience properties from one
authoritative state."

The reason is worth spelling out, because the natural implementation looks fine until it does not.
The natural implementation is:

```csharp
public bool Success { get; init; }
public SolutionSnapshot? Snapshot { get; init; }
public ImmutableArray<ProjectDiagnostic> Diagnostics { get; init; }
```

Three independent facts, and the caller who sets them decides whether they agree. They stop agreeing
the first time a solution loads with one broken project inside it: the provider has a usable
snapshot, sets `Success = true`, and attaches the failure to the project it belongs to. The result
now says the load succeeded while carrying an error, and every consumer that branches on `Success`
silently ignores it.

## Decision

`WorkspaceLoadResult` has **no public constructor** and **no `Success` property**. Two factories,
`Success(snapshot, diagnostics)` and `Failure(diagnostics)`, and the authoritative state is the pair
*(is there a snapshot?, is there an error anywhere?)*. `Status` is the total function over that pair,
computed rather than supplied:

| Snapshot | Any error | `Status` |
| --- | --- | --- |
| yes | no | `Succeeded` |
| yes | yes | `SucceededWithErrors` |
| no | yes | `Failed` |
| no | no | cannot be constructed |

`HasSnapshot` and `HasErrors` are derived from `Status`. `Snapshot` is `null` if and only if
`Status` is `Failed`.

Two details do the real work.

**"Any error anywhere" is meant literally.** `Success` flattens the snapshot's own diagnostics and
every project's diagnostics into `Diagnostics` before deciding, so the broken project above produces
`SucceededWithErrors` and cannot produce `Succeeded`. The cost is that a project-level diagnostic
appears twice — once on its `ProjectSnapshot`, once in the flattened list — which is what an
error-list consumer wants anyway.

**The fourth cell is refused rather than represented.** `Failure` with no error diagnostic throws
`ArgumentException`. A consumer shown "it failed" with nothing to display has been told nothing.

Cancellation is deliberately not a status. It is `OperationCanceledException`, which is what makes
the three values genuinely exhaustive rather than three-plus-a-special-case.

## Consequences

- A status that disagrees with the evidence is not a bug to be found in review; it is a state that
  cannot be constructed.
- A provider cannot claim success while reporting an error, even by accident, because it does not
  get to set the status at all.
- `SucceededWithErrors` is a real and common outcome, and the workspace **publishes** it: there is a
  usable snapshot, and refusing to publish it would throw away the partial result that the whole
  diagnostics policy exists to preserve.
- A caller that only wants "did anything go wrong" reads `HasErrors`; one that wants "is there
  something to show" reads `HasSnapshot`. Neither can be made to disagree with the other.
- The table is tested cell by cell, including the fourth, which is asserted to throw.

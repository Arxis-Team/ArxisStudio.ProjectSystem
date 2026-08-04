# 6. One FIFO mutation boundary, and not a semaphore

Date: 2026-08-05
Status: Accepted

## Context

`ProjectWorkspace` must serialise loads and refreshes. The task specification asks for "one
documented FIFO mutation boundary so an older refresh cannot overwrite a newer request because of
scheduler order", and permits coalescing only as a deliberate design with deterministic tests.

The obvious implementation is a `SemaphoreSlim`. It is one field, it is in the base class library,
and it gives mutual exclusion — which is most of the job.

It is the wrong choice, and the reason is narrow enough to be worth writing down: **`SemaphoreSlim`
does not promise the order in which it releases waiters.** For a host watching a folder and
submitting a refresh per change, three queued refreshes whose oldest is released last leaves the
workspace publishing a snapshot of the state from two refreshes ago — with every individual
mutation perfectly serialised. The bug survives every test that only checks exclusion.

The sibling repository met this exactly. `ArxisStudio.Markup`'s ADR 0010 documented that updates
"queue", which was a promise `SemaphoreSlim` did not make; ADR 0011 records replacing it, together
with three other findings from the same review.

## Decision

`WorkspaceMutationGate` is a port of `ArxisStudio.Markup`'s `XamlMutationGate`, minus its
non-blocking `TryEnter` (there are no synchronous edits here) and plus an internal `IsHeld` for
tests. Everything else is kept deliberately, including the parts that look like they could be
simplified:

- **Order is assigned on arrival**, in a linked list under a lock, not by whatever the thread pool
  does with the continuations.
- **A linked list rather than a queue**, so a waiter that cancels comes out of the middle instead of
  being skipped over later. Cancelling a hundred refreshes behind one slow load must not leave a
  hundred dead entries.
- **Ownership is handed over, not dropped.** `_held` stays true across the transfer, so nothing that
  arrives in between can take the turn that was just granted to somebody else.
- **The lock is held only long enough to move a link**, never across an `await`.
- **A cancelling waiter never releases ownership it was not granted.** The cancellation registration
  can only settle a waiter that has not had its turn; a waiter granted a turn and cancelled in the
  same breath gives the turn back before throwing. That is the one place ownership and cancellation
  genuinely race.
- **`RunContinuationsAsynchronously` on every waiter**, so the next operation never runs part of
  itself while the previous one is still unwinding.

Two of ADR 0011's four findings are the workspace's rather than the gate's, and are honoured in
`ProjectWorkspace`: the disposal and state checks happen **inside** the gate, with the answer read
on the way in not consulted at all; and `_disposed` is an `int` written through `Interlocked`,
because disposal and the operations it races are on different threads by design.

**No coalescing.** *N* queued refreshes run *N* provider loads. Debouncing belongs with the
file-change events that make it necessary, which is Milestone 5; designing a coalescing policy now
would mean designing it against no real trigger.

## Consequences

- FIFO order is testable without timing. `EnterAsync` queues synchronously before returning, and an
  `async` method runs synchronously to its first `await` — so as long as the gate is the first thing
  a mutation awaits, arrival order *is* submission order. `ConcurrentRefreshesRunInTheOrderTheyArrived`
  depends on this, and a refactor that put any other `await` in front would break it. That is
  intentional: the test is the alarm.
- The gate is `internal`. Nothing about it needs to be public, and a public async gate would be a
  second thing to support for no caller's benefit.
- The gate is never disposed, which is what lets an operation queued behind disposal still take its
  turn and be told the workspace is gone, rather than faulting on a disposed primitive.
- A future contributor will see a hand-written queue where a `SemaphoreSlim` would fit and will be
  tempted to simplify it. This ADR is the answer, and `docs/limitations.md` records the cost that
  comes with the choice.

# 7. A throwing subscriber is isolated

Date: 2026-08-05
Status: Accepted

## Context

`ProjectWorkspace.SnapshotChanged` is raised after publication and outside the mutation boundary.
The task specification requires that one throwing subscriber must not corrupt workspace state or
leave the boundary held, and asks for the policy to be documented as one of: propagate, aggregate,
or isolate.

Because the raise happens after the swap and after the gate is released, **no policy can corrupt
state or hold the boundary**. All three options satisfy the hard requirement, so the choice is
purely about who learns that a handler failed.

The subscribers here are independent tools watching one workspace — a project tree, an error list,
an analysis host. They do not know about each other, and none of them wrote the others.

## Decision

**Isolate.** Every subscriber is invoked inside its own `try`/`catch`. An exception from one does
not prevent the others from running and does not reach whoever called `LoadAsync` or
`RefreshAsync`. The invocation list is walked by hand rather than through the delegate's own
multicast dispatch, precisely so that one failure does not end the walk.

Every exception type is caught, `OperationCanceledException` included. The guarantee is that one
subscriber cannot affect another, and an exception type that escaped would be a hole in it.

Two reasons, and the second is the decisive one.

**Propagation starves the other subscribers.** With multicast dispatch, a handler that throws ends
the walk: the error list never learns the solution reloaded, while the project tree already has.
That is a persistent, silent divergence in *consumer* state, and it is caused by the library rather
than by the broken handler. Aggregating fixes this half — every handler runs — but not the next
part.

**The caller of a successful load is the wrong recipient.** It did not write the handler, cannot fix
it, and its own operation succeeded: the snapshot is published and the version advanced. Handing it
a third party's exception also destroys the return value it earned, because a throw happens instead
of a `return` — the caller would have to re-read `CurrentSnapshot` to recover what it should have
been given. An API where a successful operation throws and drops its result is worse than one where
a broken handler is quiet.

Roslyn, the closest analogue in the same domain, reaches the same conclusion: handler failures are
isolated from the operation rather than delivered to whoever triggered it.

## Consequences

- **A handler's failure is silent.** This is the real cost and it is not small. The mitigation is
  that it is the handler author's own bug, in their own code, discoverable in their own tests, and
  a handler that wants its failures reported catches them itself — one line, in the place that
  knows what to do about it. The core has no logging abstraction to report into, and inventing one
  for this would be a large public surface for a small problem.
- Assertions inside a handler are swallowed, so **a test must never assert inside one**: it would
  pass while proving nothing. The workspace tests record what a handler saw and assert afterwards,
  and `AHandlerSeesThePublishedStateAndAFreeGate` says so in a comment for the next person.
- The workspace stays usable after any number of handlers throw, which is tested.
- If a future consumer genuinely needs handler failures surfaced, the shape to add is a separate
  notification channel — not a change of policy here. That is deliberately not done now: there is
  no such consumer, and the specification asks for a minimal surface.

## Alternatives rejected

**Propagate the first exception**, as `ArxisStudio.Markup`'s `MarkupWorkspace` does. That works
there because a document change usually has one interested party; here it would starve the others,
and the divergence between the two is deliberate rather than accidental.

**Aggregate into an `AggregateException`** after publication. Loud, which is its appeal, and it does
run every handler. Rejected because it still throws out of a successful operation and still loses
the result.

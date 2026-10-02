# 0030. A reclaim is tested against real Avalonia, on the fixtures' own build

Date: 2026-10-03

Status: Accepted. A companion to [0010](0010-testing-a-provider-that-needs-a-real-engine.md).

## Context

The adapter's tests ran without Avalonia. They reclaimed generations of assemblies that never made a
control, and that covered the mechanics — what the reclaim forgets, how it waits, that a strong
reference is reported. Whether a *designer's* generation goes was tested only by
`UiDesigner.Demo`'s stand: a window shown and closed, a window that refuses to close, a control
subscribed to something the process keeps, a document whose session replaced another. Those are what
a generation actually meets, and only Avalonia can say what Avalonia keeps of them. Moving the order
of a swap out of the designer and into a library means testing it where the library is tested.

The same reason 0010 gives for MSBuild applies: what the rule against machine dependencies protects
is a suite that passes on one machine only. Avalonia's headless platform needs no display and no
installation — it is a package, pinned like every other.

## Decision

**The adapter's tests run on Avalonia's headless platform** (`Avalonia.Headless.XUnit`, the same
12.1.1 as Avalonia itself), with `[AvaloniaFact]` wherever an object of a generation is made. The
application has no theme: what is tested is what a generation leaves in the process, not how it
looks.

**The project's own controls are a fixtures library built beside the tests and never referenced as an
assembly** (`ReferenceOutputAssembly="false"`). Its output and documents are copied under the tests'
`fixtures/`, and each test lays them out as a project of its own, from which a generation loads them
into a collectible context as it loads a user's build. In the default context they could never be
reclaimed, and every test proving a generation gone would prove nothing. The fixtures hold what a
designer meets: an `x:Class` control and window with compiled markup, a control that subscribes to
the process, a window that refuses to close.

**A test keeps names and weak references of what a generation built, never the objects.** Every
object is made and dropped in a method of its own, not inlined, because a local of an asynchronous
test lives as long as the test and would hold what the test is proving gone.

**Every test class that makes a generation of the fixtures belongs to one collection that does not run
in parallel.** Two generations of one assembly alive together are the failure ADR 0029 records, and
xunit runs classes in parallel — on the headless dispatcher two classes interleave at every await.

## Consequences

- A change to what the reclaim forgets, or to what a designer has to close before asking, fails a
  test here rather than on the stand.
- The suite pays for real collections: a reclaim is at least one forced collection, and a held one
  waits the reclaim's patience — about two seconds — by design.
- The stand keeps what only a real designer has: a renderer drawing frames, input, focus, the
  designer's own parts. The tests do not replace it; they take from it what can be asserted in a
  process of their own.

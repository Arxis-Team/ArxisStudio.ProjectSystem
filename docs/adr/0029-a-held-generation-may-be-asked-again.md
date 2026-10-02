# 0029. A held generation may be asked again, and none is born beside another of its assemblies

Date: 2026-10-03

Status: Accepted. Amends [0023](0023-a-generation-is-reclaimed-before-its-successor-is-born.md).

## Context

[ADR 0023](0023-a-generation-is-reclaimed-before-its-successor-is-born.md) proves a generation gone
before its successor is created, and falls back to a restart when the proof fails. Testing that
against real Avalonia ([0030](0030-a-reclaim-is-tested-against-real-avalonia.md)) and building a
host on it found two generations 0023 did not see, each of which put two copies of a type in the
process all the same:

- **A predecessor somebody else retired.** A generation disposed rather than reclaimed, or one an
  earlier host made — a designer closing a project and opening it again in the same process. It was
  unloaded, not collected, and Avalonia's runtime compiler resolves a document's names in every
  assembly the process has loaded, by name: the new generation's form failed with *Unable to
  substitute T with T*, its `x:Class` resolved to the old copy. `AssemblyLoadContext.All` does not
  show such a predecessor — a context leaves that list the moment it starts unloading, while its
  assemblies stay loaded until it is collected.
- **A generation found held whose holder let go later.** A window that refused to close closes at
  last; a control drops its subscription. Asked again, the reclaim repeated its first answer — and
  asked again with a fresh look it would still have said "held", because letting go touched the
  holder's type: a window closing reads and writes its properties, and Avalonia wrote the type back
  into the caches the first reclaim had emptied. Measured: the window collected, its generation's
  context still alive after twenty more collections.

## Decision

**A generation found held may be asked again, and the second question is a new one.**
`TryReclaimAsync` keeps what it forgot the first time — weakly — and a later call runs the cleanup
again over whatever of the generation is still there, then waits for the collection as the first call
did. One found gone stays gone; a caller asking while another asks is told "not yet".

**No generation is created beside another of the same assemblies.** Before a host makes a generation
it waits for every collectible context holding an assembly of the names its projects build to be
collected — asked of the domain's assemblies, not of `AssemblyLoadContext.All` — for as long as a
reclaim would wait (`WaitForPredecessorsAsync`). When one stays, the honest answer is the one 0023
gives for its own predecessor: a new process.

## Consequences

- A host that disposes its generation should reclaim it, not only unload it, so that its successor —
  the next host over the same project — finds nothing to wait for.
- Two hosts over one project in one process cannot both have a generation; the second one is told to
  restart while the first one's generation is alive.
- A test that loads the same build into generations runs one at a time: two alive together are
  exactly the failure this records.
- Asking again is not a way out of a restart for a designer: a generation that stayed once may stay
  again, and the designer cannot know when its holder lets go. It is what makes a held generation's
  end observable, for a host that knows, and for the tests.

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

## Amendment, 2026-10-04: the cleanup runs again after every turn a reclaim gives the dispatcher

The second question existed because letting go touches the holder's type. The same happens inside a
single question: a window of the project, shown and closed just before the reclaim, does its last work
in the dispatcher turns the reclaim itself gives the user interface, and asks its properties about its
own type on the way. Once anything has overridden a property's metadata — a button's class constructor
overrides `InputElement.Focusable`, so every application with a button has — the property remembers
every type it is asked about in a cache keyed by the type. Measured with a walk of the heap during the
wait: `Focusable` held the fixtures' window, put back after the cleanup had emptied it, and nothing
else held the generation; asked a second time, it went.

So `TryReclaimAsync` runs the cleanup again over whatever of the generation is still there after every
round's dispatcher turn and pause, not only when asked a second time. A generation that something really
holds still answers "held"; one that only wrote itself back on the way out goes in the same question.
The adapter's test runs a button's class constructor, shows and closes a window of the fixtures and
reclaims its generation once; without the repeated cleanup it answers that the generation stayed — and
in the studio, where buttons always exist, every form shown on the canvas would have ended its swap in a
restart.

## Amendment, 2026-10-06: a predecessor of other assemblies that declares the same types

The rule waited for a predecessor of the *same assemblies*, and the runtime compiler resolves more than
assembly names across the process. A type written against a CLR namespace — `using:App` — is looked up by
its full name in every assembly the process has loaded, and the first one that declares it answers,
whatever that assembly is called. ArxisStudio's tests found it. One test held a generation past a swap and
let go of it as it ended; the next test's generation — another assembly name, the same `App.Badge` — was
created while the first was still unloading, and its form was built from the first one's `Badge`. Building
that control wrote its type back into Avalonia's caches, where the second generation's reclaim does not
look, so the first stayed in the process for good, and every successor of the second showed its `Badge`
too, each swap reporting its own generation reclaimed. A designer meets the same when a solution follows
another that declares the same types under another assembly name — a renamed copy — while the first one's
generation is still in the process.

So the wait also covers every collectible assembly that declares a type the design set's builds declare.
The types are read from the built files' metadata before anything loads, and a predecessor is asked for
each by its full name (`Assembly.GetType`), the question the compiler asks. They are the public, top-level
types whose names a document can write. Every assembly with compiled markup declares the same seven
helpers of Avalonia's compiler — five internal, such as `CompiledAvaloniaXaml.XamlIlContext`, and two
public whose names no document can write, `!XamlLoader` and `!AvaloniaResources` — and counting them made
any two projects one another's predecessors. Over thirty of ArxisStudio's own assemblies and the fixtures'
build, those seven were the only top-level types any two of them shared. A predecessor that goes is
waited for, as one of the same assemblies is; one that stays is a restart, `GenerationStillHeld`.

Not compared: one XAML namespace that two assemblies map (`XmlnsDefinition`) to CLR namespaces of
different names, each declaring a type of the same simple name. The compiler would answer that name from
the first assembly as well. Seeing it means reading every collectible assembly's mappings on every round,
and it takes a renamed library that renamed its namespaces and kept its types' names; it is listed in the
limitations.

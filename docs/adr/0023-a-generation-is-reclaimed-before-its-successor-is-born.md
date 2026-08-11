# 0023. A generation is reclaimed before its successor is born

Date: 2026-08-11

Status: Accepted. Amends [0021](0021-a-run-holds-one-generation-of-a-projects-types.md)

## Context

[ADR 0021](0021-a-run-holds-one-generation-of-a-projects-types.md) held a run to one generation of
a project's types and answered a code change with a restart, because a superseded generation was
measured to survive: creating one control registers its type in Avalonia's process-wide
`AvaloniaPropertyRegistry`, that registration outlived the unload, and the runtime XAML compiler
then answered a simple name with the *first* of two copies — `Unable to substitute X with X`. It
closed by naming the condition for revisiting: *"If a future Avalonia offers a per-context XAML
compiler, or a way to scope assembly resolution to a load context, this decision should be
revisited."*

Avalonia 12.1.1 supplies the missing half. `AvaloniaPropertyRegistry.UnregisterByModule` is public,
and `IAssemblyDescriptorResolver.InvalidateAssemblyCache` lets the asset loader forget an assembly
by name. Neither is sufficient alone, and what else is needed was found by measurement rather than
by reading — a harness (`--reclaim`) that opens forms in the real studio, tears them down, and asks
whether the generation is provably gone, reporting what still holds it when it is not.

What that harness established, in order:

1. **`UnregisterByModule` must be given only property owners.** Handed every type in the assembly —
   compiler-generated XAML helpers included — it answers `False` and changes nothing.
2. **It answers `True` and still leaves two residues**, each of which alone keeps the generation:
   the type as a key in the by-type dictionary, kept there by the properties it merely *inherits*,
   and every property it declared in the by-identifier dictionary the method never touches.
3. **The runtime compiler's emitted state must be reset for the *dying* context.** ADR 0020 resets
   it when *entering* a generation; at swap time nothing has entered a successor yet, so the stale
   statics root the dying context's dynamic assembly by themselves.
4. **`TypeDescriptor` keeps a converter per type, for the life of the process**, and Markup asks it
   for one whenever a value is converted from text. `TypeDescriptor.Refresh(assembly)` clears it.
5. **The asset loader's assembly-by-name cache holds the assembly** as soon as anything is loaded
   through `avares:` — an `Icon` on a window is enough.
6. **A `Window` the generation produced must be closed.** Constructing one puts it in the windowing
   platform's own static list, and retiring the session does not take it out.
7. **A form left open holds its generation** through the container that shows it. Taking it off the
   canvas is not enough; it has to be closed and built again, which is why a swap remembers what a
   form was rather than keeping the object.
8. **The context must let go of its own load context**, and **nothing may name the generation from
   a live frame**: an asynchronous method keeps its locals in an object that lives as long as the
   method does, so the first drafts of both the measurement and the reclaim measured their own
   locals. A synchronous, non-inlined method is the one storage the collector is sure about.

The last one has a sharp edge worth recording separately, because it cost the most: the environment
holds the generation's assemblies. `ProjectXamlEnvironment` hands the type resolver
`Searchable(context)` — a list of loaded assemblies — so a host still holding `_environment` while
asking for a reclaim is holding every assembly in it, and the answer is an honest "still held" for a
generation nothing else wants.

## Decision

`ProjectAssemblyContext` gains `TryReclaimAsync`, which unloads a generation, removes what the
process would otherwise keep of it, and answers whether it is provably gone — the proof being that
every assembly it loaded, and the load context itself, are unreachable after a bounded number of
collections.

**A successor is created only after that proof.** This preserves ADR 0021's real invariant — at most
one live copy of a project's types — while changing how it is delivered: the copy may now be
replaced within a run. `Unable to substitute` cannot return, because the "first match wins"
resolvers that produced it have nothing to choose between.

**When the proof fails, the studio restarts**, exactly as it did before. That path is not a
regression to be removed later; it is the honest answer for a generation something still holds — a
user control that started a timer, a subscription nothing released — and it is what makes the swap
safe to attempt at all.

The reflection posture is [ADR 0020](0020-the-adapter-resets-avalonias-runtime-xaml-compiler.md)'s:
every member is found by name **and** checked for shape, including the public ones, so a future
Avalonia that changes them degrades to a reclaim that answers "no" — that is, to yesterday's
restart — rather than to a designer that throws.

## Alternatives

**Keep restarting.** Rejected because the API 0021 asked for now exists and the measurement says it
works. It is not abandoned, though: it is what the gate falls back to, so the decision re-tests
itself on every swap.

**A preview host in a child process**, the way Avalonia's own previewer works, with the studio
talking to it over a protocol and restarting it invisibly. This remains the strategically stronger
answer — it makes *every* kind of stale state cheap to discard, not just the kinds this reclaim
knows about. It is not built here because the cost is a different order: the design surface, the
load sessions and the object map would all move into the host, and every `Control` identity the
editor, inspector and hierarchy pass around by reference would have to become a marshalled handle.
Recorded as the direction to take if the reclaim's failure modes ever become common.

## Consequences

- A code change is answered in place: the studio builds, reclaims, and rebuilds the open forms from
  the documents it is holding. Tabs, their order, the active form, unsaved edits, undo history,
  canvas geometry and the selection all survive; the window does not blink.
- **Unsaved work no longer blocks the reload.** A restart discarded it, so it refused; a swap
  rebuilds from the in-memory documents, so there is nothing to lose. The refusal moves to the
  fallback, where it still belongs.
- The order of release is part of the contract, not an implementation detail. The host lets go of
  the environment and the context fields *before* asking for the reclaim, closes the generation's
  windows, and closes its forms — `docs/limitations.md` says so, because a host that gets this
  wrong sees a swap that always falls back and never a wrong pixel.
- At most one generation can leak, and only when the gate says no — after which the restart clears
  the process anyway.
- The measurement stays: `--reclaim` is a permanent harness step, run against the scaffolded ladder
  or against a real solution, and it is what the next Avalonia upgrade should be tested with.

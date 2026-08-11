# 0022. An embedded control's markup follows the live document

Date: 2026-08-11

Status: Accepted. Amends [0021](0021-a-run-holds-one-generation-of-a-projects-types.md)

## Context

ADR 0021 split the world in two: markup is read from the file every time, types once per run. One
case sat on the wrong side of that line. A form that places a project's own control —
`<views:MyControl />` — shows the control the way it was *compiled*, because the placed instance
is constructed by Avalonia and its constructor loads the markup baked into the generation's
assembly. Markup, by any honest reading, but delivered through a type — so editing
`MyControl.axaml` changed every open preview except the ones that placed it, and the studio's
answer was the restart nudge: save the control, be told which forms are behind, press Reload,
watch the studio relaunch. For the commonest act of composition a designer exists for, the
restart was not answering a *type* question at all. Nothing about `MyControl`'s code had changed.

Meanwhile the sibling repository grew the seam this needs (its ADR 0014): Avalonia's XAML
compiler emits into every `x:Class` type a populate-override hook, and Markup's
`XamlLivePopulation` installs a populate there built from a live `XamlDocument` — prepared
through the same environment the sessions load in, entered through the same
`IXamlCompilationScope`, falling back to the compiled markup when the document cannot populate.
What Markup cannot know is which document is which type's: pairing a project's documents with a
generation's types is precisely the adapter's kind of knowledge.

## Decision

The adapter gains `ProjectXamlPopulation`, created over a generation and its environment. A host
hands it documents; it reads each document's `x:Class`, resolves the class against the
generation's rebuildable assemblies — the project's output and its project references, the same
list the type resolver searches — and registers the document with Markup's `XamlLivePopulation`.
A document naming no class, or a class the generation does not have, is answered with `null`:
both are ordinary, and the second is ADR 0021's restart case, not this type's business.

The host's part of the contract:

- Register a document on open, after every applied edit, and after every reload from disk —
  unsaved edits are the point, not an accident.
- Rebuild the previews that place the control, because population changes constructions from now
  on and never instances already on screen.
- Dispose the registry **before** the generation. The override field roots the delegate, the
  delegate roots the registry, and an undisposed registry is a collectible context that never
  collects.

`ProjectAssemblyContext` also gains the staleness question this sharpens:
`IsCurrentOnDisk()` compares the rebuildable files against a stamp taken at creation, which is
how a host asks, after a build, whether the generation's *types* are now behind the code — the
question the restart still answers.

## Consequences

- Editing a control's markup — in the studio or in the other editor, saved or not — reaches every
  preview that places the control, without a restart. The consequence in ADR 0021 that said
  otherwise is amended, not the decision: there is still one generation of types per run.
- The restart nudge stops firing for markup and keeps firing for what it was always about: a
  class added or changed since the project was opened. `IsCurrentOnDisk` is what tells a host the
  difference after a build.
- A registered document that fails to compile costs freshness, never a form: the placed instance
  falls back to its compiled markup and `PopulationFailed` says so.
- The pairing is by `x:Class` against rebuildable assemblies only. A package control's markup is
  not overridable this way — nobody is editing its documents here, and package assemblies could
  not be released anyway (they load into the default context and stay).

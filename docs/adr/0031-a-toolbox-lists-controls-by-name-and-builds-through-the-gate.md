# 0031. A toolbox lists controls by name, and placing one not built yet builds it through the gate

Date: 2026-10-03

Status: Accepted. Extends [0028](0028-the-design-host-replaces-a-generation-in-order.md).

## Context

A designer beside an IDE offers the project's own controls, and the one it most needs to offer is the
one the IDE has just written: a new `x:Class` document whose class no build has produced. Each obvious
way of doing that goes wrong.

- **A toolbox of types.** Reflecting over the generation's assemblies hands the toolbox `Type` objects,
  and a toolbox outlives the generation it was filled from — it is on screen across the swap. A `Type`
  in it keeps the old generation in the process, and the swap ends in a restart.
- **Inserting first, building later.** An element of a class not built yet is something no session can
  build. The form goes `Behind`, saying the type is not found, until a build and a swap come round, and
  the history holds a step for an element that never showed.
- **Forcing the swap.** `SwapAsync` swaps whatever holds it off. A drop that forced one would cut
  through a gesture or a value half typed — exactly what the gate exists to protect.

Beside these, the host rebuilt every form that places a control when the IDE saved the control's own
markup: a new session, a new window root, the author's window constructor run again — for one control.

## Decision

**Placeable controls are names.** `GetPlaceableControlsAsync(project)` lists, in a turn, what the live
generation built of the project and the projects it references: creatable controls that are not
windows, read through Markup's catalog, which holds no type. It also lists, as not built, the documents
among those projects whose `x:Class` the generation does not have, written in `using:` their namespace.
A document whose root resolves to a window is left out. `ProjectControlInfo` carries the class, the
element name, the namespace, the prefix, the kinds, the owning project, the control's own document,
and whether it is built.

**Placing a control that is not built builds it, through the gate.** `EnsureBuiltAsync(control)`
answers at once for a class the live generation has.

- Otherwise it builds the control's project — the top projects of what references it, as every build
  of the host does.
- Then it waits for the generation the build calls for, as long as anything holds the swap off. It
  never swaps past a deferral.
- The answer is whether the live generation has the class then. A failed build, a class the build did
  not produce, and a restart required are all `false`.

**What a drop meant waits as names.** The designer keeps the document, the element path and the index
across the wait, because the swap rebuilds every form. It inserts once `EnsureBuiltAsync` says yes, so
the document never holds an element nothing can build.

**A placed control's markup rebuilds only the elements that place it.** The host asks each form that
places the control to build those elements again — `XamlLiveDocument.RebuildAsync` with a chooser, in
Markup — and the form keeps its session and its root. A form whose root is the control is built whole,
which is all its session can do.

## Consequences

- An unbuilt control's namespace is `using:`. Once it is built, its assembly may map it to another, and
  the next listing says so. A drop written in `using:` resolves either way.
- The kinds of an unbuilt control are what its root says it will be: a user control, or a control.
- `EnsureBuiltAsync` waits as long as a deferral lasts. A drop made during a gesture is placed when the
  gesture lets go.
- Public API added: `ProjectControlInfo`, `ProjectDesignHost.GetPlaceableControlsAsync` and
  `ProjectDesignHost.EnsureBuiltAsync`.
- Tests: `DesignHostControlsTests` covers the listing, a build skipped for a loaded class, a build and a
  swap for one that is not, a failed build, and a deferral that holds the wait.
  `DesignHostChangeTests` asserts that a form placing a saved control keeps its session.

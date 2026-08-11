# 0021. A run holds one generation of a project's types

Date: 2026-08-11

Status: Accepted, amended by [0022](0022-an-embedded-controls-markup-follows-the-live-document.md)

## Context

The designer loads a project's assemblies into a collectible `AssemblyLoadContext` so that a form
can use the project's own controls. When the project is rebuilt — and a designer rebuilds it often,
because opening a form whose code-behind changed requires it — the obvious move is to build a new
context from the new output and carry on.

That is what the sample did, and it produced a failure with an unforgettable shape:

```text
Unable to substitute StressApp:StressApp.Views.MainWindow
                with StressApp:StressApp.Views.MainWindow
```

The same name on both sides, because they were never the same type. `AvaloniaXamlIlCompiler.Parse`
compares the type it resolves from `x:Class` against the type of the root instance it is asked to
populate; the first comes from the compiler's own snapshot of the loaded assemblies, the second
from the generation the designer means.

Three hypotheses were tested against the running designer rather than argued about:

1. *A stale compiled populate method is cached.* Refuted: the runtime compiler declares eight
   statics, all `_sre*` and `_ignoresAccessChecksFromAttribute`, and no cache of compiled methods
   survives a generation.
2. *The old copy is still loaded because the collector has not run yet.* Refuted: retiring every
   session, dropping every reference and collecting three times left the copy exactly where it was.
3. *Superseded generations are never collected at all.* Confirmed, by counting: after three
   generations, all three were still in the process, and the compiler resolved `x:Class` to the
   **first** — `FindAssembly` answers a simple name with the first match.

The third is not a leak in this repository. Creating one control from a generation registers its
type in Avalonia's process-wide property registry, and that registration outlives the context that
was unloaded. A designer that makes a generation per build accumulates every generation it ever
made, and the compiler goes on answering with the oldest.

## Decision

A run holds **one generation of a project's types**, created when the project is opened and kept
until it is closed. A rebuild does not create a second one, and open forms are never moved between
generations.

What follows from that, and is the point of the arrangement:

- **Markup is read from the file every time.** Layout — the whole of what a form designer is for —
  is always the file's, including edits made in the other editor.
- **Types are read once.** A class added or changed since the project was opened is not in this
  run, and cannot be put there.

When the second half is what somebody has just run into — a load that fails because `x:Class` names
a class no assembly has, or a saved control that other open forms place — the studio says so and
offers `RestartCommand`: the same project, the same form, a new process. It is one press, and it is
the only honest answer a process can give about types it has already loaded.

## Consequences

- The "unable to substitute" failure cannot occur, because there is never a second copy to resolve.
- A form created during a run does not open in that run. The studio detects the case by its
  diagnostic and offers the reload; it does not fail silently, and it does not pretend to reload
  the types in place.
- ~~An embedded control's appearance follows the assembly, not the file, so a form that places one
  keeps showing the compiled shape until a reload. The studio names the forms that are behind.~~
  Amended by [ADR 0022](0022-an-embedded-controls-markup-follows-the-live-document.md): an embedded
  control's *markup* now follows the live document through `ProjectXamlPopulation`; what still
  follows the assembly — and still answers to a restart — is the control's *code*.
- Restarting is the designer's answer to stale types, which is what the Avalonia previewer does for
  the same reason — it runs the preview in a process it can restart.
- If a future Avalonia offers a per-context XAML compiler, or a way to scope assembly resolution to
  a load context, this decision should be revisited: the constraint is that compiler's global
  snapshot, not anything in this design.

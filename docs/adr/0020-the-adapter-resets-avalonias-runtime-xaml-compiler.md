# 0020. The adapter resets Avalonia's runtime XAML compiler between generations

Date: 2026-08-10

Status: Accepted

## Context

The adapter exists so that a rebuilt project assembly is a *different* assembly: each build
generation goes into a collectible `AssemblyLoadContext` of its own, and a form loaded under one
generation resolves its types there (ADR 0018, and `ProjectAssemblyContext` itself).

Markup loads documents through Avalonia's runtime XAML compiler
(`AvaloniaRuntimeXamlLoader`). That compiler keeps one reflection-emit type system for the whole
process — static fields on `AvaloniaXamlIlRuntimeCompiler`, created on the first load and never
replaced — and it remembers assemblies by simple name. The code it generates references the
document's types through that cache.

The two designs meet badly. A designer creates a form, builds the project, and loads: the class
exists, the generation's context resolves it, and object creation still fails with
`Could not load type 'App.NewWindow' from assembly 'App'` — because the generated code bound
`App` to the copy the compiler saw first, which predates the class. The environment answered
correctly and was overruled by a process-wide static. This was found empirically: a first-chance
stack trace pinned the throw inside `AvaloniaXamlIlRuntimeCompiler.LoadOrPopulate`, while an
`Activator.CreateInstance` on the same type object, moments earlier, succeeded.

Avalonia 12.1.1 offers no public way to reset or scope that state.

## Decision

`ProjectAssemblyContext` implements Markup's `IXamlCompilationScope`, and
`ProjectXamlEnvironment.Create` supplies the context as the environment's `CompilationScope` — so
the session itself brackets every compilation it performs, and no host calls anything by hand.
(The first version of this decision had the host wrap every load in `EnterLoadScope()` manually;
Markup's ADR 0013 records why that moved into the environment: correctness was depending on every
caller remembering, at every call site, forever.)

Entering the scope does two things:

- If the compiler's emitted state lives in a different `AssemblyLoadContext` than this generation's
  — an older generation, or the default — the static fields are cleared by reflection, so the next
  load rebuilds the compiler's state from scratch. Avalonia 12.1.1 declares eight: the seven named
  `_sre*` and `_ignoresAccessChecksFromAttribute`, a type emitted into the dynamic assembly and as
  generation-bound as the rest.
- Contextual reflection is entered on this generation's context for the length of the load, so the
  rebuilt dynamic assembly is created *inside* it and every assembly reference the generated code
  makes binds in this generation first.

Two open forms from two generations each re-enter their own context; the compiler re-initialises
when the generation actually changes, and that cost is accepted.

The host keeps a superseded generation alive until the last form loaded under it closes. Disposing
it on supersession — the obvious simpler rule — breaks the first edit made on a still-open form
from the previous generation.

## Consequences

- A form created after a rebuild loads, which is the scenario the designer exists for.
- The reset touches non-public fields by name, verified against the published Avalonia 12.1.1
  sources: `InitializeSre` re-creates each field it finds null, `SreTypeSystem`'s constructor
  snapshots every loaded assembly and `FindAssembly` answers a simple name with the first
  match, and `DefineDynamicAssembly` honours contextual reflection — which is what makes
  entering it per generation sufficient. If a future Avalonia renames the fields, the reset
  quietly does nothing and the stale-cache behaviour returns — degraded, but not broken in any
  new way.
- The compiler's re-initialisation on generation change re-emits its support types. That is paid
  once per rebuild, in a tool whose whole purpose is rebuilding.
- If Avalonia grows a public reset or per-context compiler, this scope should shrink to calling it.

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

## Amendment, 2026-10-04: a generation that loaded nothing does not take the compiler in

A design set whose builds are not there — the first design build failed, or a project has no output
yet — makes a generation that loads no assembly at all. Entering its scope put the compiler's dynamic
assembly into it, and that assembly became the context's only one. The runtime does not unload a
collectible context whose only assembly is a dynamic one: measured outside Avalonia and the adapter, a
context with one `RunAndCollect` assembly defined under contextual reflection stayed alive through
eighty-four forced collections, while the same context with an ordinary assembly loaded first went in
the first one. In the adapter, the first successful build after a failed one answered with a restart.

So `EnterLoadScope` checks the context first. One that holds no assembly enters nothing: its
documents name nothing of the project and compile against the process, in the default context — and
the compiler's state is cleared again when that scope ends, because state left in the default context
outlives every generation, and the next one is what it would hold. A generation that loaded anything
is entered as before. The adapter's test compiles markup in the scope of a generation of nothing and
reclaims it; without the check it answers that the generation stayed.

## Amendment, 2026-10-13: what a generation of nothing compiled stays with it

Clearing the compiler's state let go of more than the state. A designer asking a form loaded under a
generation of nothing for one of its resources took the process down with an access violation: the
user interface thread executed an address in freed memory. A dump showed the call — Avalonia's
`PointerDeferredContent<T>`, the deferred value of a keyed resource nobody had asked for yet, calling
its builder. The builder is a function pointer into the compiler's dynamic assembly, and a pointer
keeps no assembly alive. In the default context that assembly is collectible and its own; once the
scope cleared the compiler's fields, nothing referred to it, and a collection took it while the form
it had built still stood. Any first look at such a resource — a style's pointer-over state, a tool
reading what the form declares — then ran freed memory, and it was the process that ended, not the
form. A generation that loaded something never had the hole: its dynamic assembly lives in its
context, and the context stays until the generation is unloaded.

So the scope of a generation that loaded nothing hands the assembly it lets the compiler go of to the
generation, which keeps it until it is disposed — the lifetime a context gives the code emitted into
it. The compiler's state is still cleared when every such scope ends, for the reason above.

- A generation of nothing keeps one dynamic assembly per load: each scope re-initialises the
  compiler, so each load emits its support types again. That is memory a broken build accumulates
  while it is edited, given back when the first successful build replaces the generation, or when the
  solution closes.
- The adapter's test loads a form with a resource it does not ask for, collects, and asks. It checks
  the code first, through a weak reference — without the hand-over the code is gone while the form
  stands, and asking would end the test process rather than fail the test — and then that the code
  goes once the generation is reclaimed.

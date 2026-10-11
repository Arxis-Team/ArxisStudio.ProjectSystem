# 36. An assembly without its compiled markup is rebuilt

Date: 2026-10-11
Status: Accepted. Extends [0028](0028-the-design-host-replaces-a-generation-in-order.md)

## Context

Avalonia's XAML compiler runs after the C# compiler and only when it does: `CompileAvaloniaXaml` is in
`TargetsTriggeredByCompilation`, which `CoreCompile` calls after `Csc` ran. Until it runs, every
generated `InitializeComponent` calls `AvaloniaXamlLoader.Load(this)`, a stub that throws "No
precompiled XAML found"; the XAML compiler replaces those calls with the compiled populate.

When the XAML compiler fails, the build fails, and the C# compiler's assembly stays in the intermediate
folder with a fresh time. The next build finds it newer than every source, skips `Csc` — and with it the
XAML compiler — copies the assembly to the output and succeeds. Every later build does the same until a
C# source or a markup file changes. Reproduced with `dotnet build` and Avalonia 12.1.2: the first build
fails with `AVLN2000`, the second prints "Build succeeded, 0 Error(s)" over an assembly with no type of
the XAML compiler in it. An IDE's own build has the same hole; a designer cannot live with it, because
it constructs the forms.

A designer met it. A form carried two attributes the compiler rejects, the design build failed, the next
one succeeded, and every form of the project — not only the broken one — failed to construct, with
nothing left to say why: the build was green.

## Decision

**A build that succeeded is checked for the markup its projects compile.** A project of the design set
with markup items (`AvaloniaXaml`) whose built assembly declares no type in `CompiledAvaloniaXaml` —
every assembly the compiler wrote into has seven, `!XamlLoader` among them — is unfinished. The host
rebuilds the top projects of the unfinished ones (`Rebuild`, with the build properties), and the rebuild's
status and diagnostics are the build's. A rebuild that fails on the markup says what the compiler said,
and says it again on every later build until the markup is answered.

**Starting counts an unfinished output as out of date.** Its time says it is newer than everything, which
is exactly the lie; the check is what tells a studio opened on that state to build before its first
generation.

**Read from metadata, never loaded.** The question is asked of the file with `PEReader`, shared for
reading and writing; a file that cannot be read is not judged and nothing is rebuilt over it.

**A project that compiles no markup is rebuilt once.** One whose rebuild succeeded and still has no
compiled markup loads its markup another way; the host remembers it for its life and does not rebuild it
again.

## Consequences

- A designer shows the markup compiler's errors for as long as they stand, instead of a green build and
  forms that cannot be constructed.
- A rebuild costs a full compile of the projects it names, and is paid only after an unfinished build.
- The check knows Avalonia's compiler by the namespace it emits into, as `ProjectAssemblyContext` already
  does when it reads public types; the adapter is the package allowed that knowledge.

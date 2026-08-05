# 15. Invalidation is not transitive

Date: 2026-08-05
Status: Accepted

## Context

Milestone 5 asks for an invalidation graph: a file changed, so which projects are now stale?

The obvious shape is the one a build uses. A build is transitive — rebuilding `App` when `Library`
changes is not optional — so the natural assumption is that a change to `Library` makes `App`'s
snapshot stale as well, and that invalidation must walk the project-reference graph transitively.

That assumption is wrong here, and it is worth writing down precisely because it looks so obviously
right.

**Evaluating a project does not read the projects it references.** MSBuild resolves a
`ProjectReference` during a build, in the `ResolveProjectReferences` target. During evaluation the
reference is an item and nothing more: no file is opened, no property of the referenced project is
consulted. This is visible in `ProjectSnapshot.EvaluationInputs`, which is built from what the
evaluation actually imported — a referencing project's inputs do not contain the referenced
project's file, because it was never read.

So if `Library.csproj` changes, `App`'s snapshot is still correct in every field. It still says it
references `Library`; the identity it holds is unchanged; the packages it resolved are its own.
Re-evaluating `App` would produce exactly what it already said.

## Decision

`SolutionSnapshot.Invalidate` maps changed paths to projects through
`ProjectSnapshot.EvaluationInputs` and stops there. No closure over project references.

A change to the entry point is the one case that widens rather than narrows: a solution that changed
may have gained or lost projects, so which of the *current* ones are stale is no longer the
question, and the answer is `EntryPoint` — load it again. A standalone project entry point is not
treated this way, because a project file cannot add a project to a workspace; it is stale like any
other project, through its own inputs, where it appears because a project is always its own input.

## Consequences

- **The common case costs nothing.** Most of what changes in an open repository is source code, and
  source code does not change what a project *says*. Those changes map to no project at all and the
  answer is `None`.
- **One file can stale many projects.** `Directory.Build.props` is an input of everything beneath
  it, which is the many-to-one relationship that makes this worth computing rather than assuming.
- **Build freshness is a different graph and genuinely is transitive.** Whether `App.dll` on disk is
  current does depend on `Library.dll`, and on source files, and on timestamps. Nothing here answers
  that, and the answer must not be smuggled into this one — they invalidate different things from
  different inputs.
- **If a future provider does read referenced projects during evaluation, this stops being true.**
  The rule is not "references never matter"; it is "our provider's evaluation does not read them,
  and it says so in its evaluation inputs". A provider that did read them would list them as inputs
  and the existing mapping would handle it without changing — which is why the mapping is over
  declared inputs rather than over a hard-coded notion of what a project depends on.
- Invalidation is computed against an immutable snapshot and returns an immutable answer, so it is
  pure: every case is tested exactly, with no disk, no timer and no thread.

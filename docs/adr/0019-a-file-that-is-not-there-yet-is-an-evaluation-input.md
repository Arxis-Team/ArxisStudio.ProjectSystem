# 19. A file that is not there yet is an evaluation input

Date: 2026-08-06
Status: Accepted

## Context

`ProjectSnapshot.EvaluationInputs` answers "when does this snapshot stop being true", and it was
built from what MSBuild reported it had imported. That is exact for every import a project names,
and silently incomplete for the ones MSBuild finds by looking.

`Directory.Build.props`, `Directory.Build.targets` and `Directory.Packages.props` are found by
walking up from the project directory. A project with none above it therefore imports none, so its
evaluation inputs said nothing about them — and somebody creating one changed how that project
evaluates without changing any file the project had declared it depended on. The workspace stayed
stale-free and wrong. `docs/limitations.md` carried this as a known gap and deferred it to "the
watching work", which now exists.

Two facts, measured against a real MSBuild rather than assumed, decide the shape:

- **The walk stops at the first file it finds, and a nearer file takes over.** With a
  `Directory.Build.props` two directories above a project, adding one beside the project switched
  the evaluated properties to the new file and the outer one stopped being imported at all. So the
  interesting set is not "where the file is" but "everywhere the walk passed through".
- **A property names the file that was found.** `DirectoryBuildPropsPath` and its two siblings are
  set to the found path, and empty when nothing was found. So the provider can learn where the walk
  stopped instead of re-deriving it, and can read the file's *name* from it — which is how a
  repository that renames the convention is followed rather than second-guessed.

The unbounded case is the one that needs a decision. When nothing is found, MSBuild's walk ran to
the root of the drive, and repeating that faithfully would name `C:\Directory.Build.props` for every
project a few directories down.

## Decision

**A provider names every place a convention-based import would be looked for, and those paths are
ordinary evaluation inputs.**

No new collection and no new concept. `EvaluationInputs` already documented that some of its entries
name files that are not there — a project that has never been restored lists where its restore
output would be — and this is the same rule applied to a second case. `SolutionSnapshot.Invalidate`
therefore needed no change at all, and the core learned nothing about MSBuild's conventions: the
provider supplies the facts, the core compares them.

**The walk is bounded by the entry point's directory**, passed to the translator by the provider.
That is what the caller opened, and therefore what it has said it is interested in. Where MSBuild
did find a file the bound is exact and the ceiling is not consulted — nothing above the file in use
can be reached.

The rejected alternative was a parallel `PotentialEvaluationInputs` collection. It would have forced
every consumer to learn a second concept and to remember to watch both, to answer a question that
already had a property: *what makes this snapshot untrue*. A file that would be read if it appeared
makes the snapshot exactly as untrue as one that was read and then changed.

## Consequences

A `Directory.Build.props` created anywhere between a project and the file it currently uses now
makes that project stale, which is what a person editing a repository expects and what nothing
previously reported.

Evaluation inputs grew. A project one directory below the file in use gains six paths, most of which
name nothing; the sample's watched-file count for a solution with no directory build files went from
three to nine. `FileWatchPlan` absorbs this without new watchers, because every candidate directory
exists by construction — they are ancestors of a directory that exists — so each is a shallow watch
of a directory that was very likely being watched already.

A file created above the opened solution is still missed, and a project referenced from outside the
solution's tree gets only its own directory. Both are recorded in `docs/limitations.md`. Both are a
host's to close by watching more, which is precisely the composition
[ADR 0016](0016-watching-belongs-with-the-provider.md) leaves to it.

The three properties join `SurfacedProperties` and so appear on `ProjectSnapshot.Properties`, the
same way `NetCoreRoot` and `NuGetPackageRoot` already do for the toolchain filter. That is a small
widening of a curated list, with a reason attached to it.

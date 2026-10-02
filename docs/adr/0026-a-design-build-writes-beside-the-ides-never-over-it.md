# 0026. A design build writes beside the IDE's, never over it

Date: 2026-10-03

Status: Accepted.

## Context

The form designer builds the projects it shows, and the IDE beside it builds the same projects. Both
default to the same folders — `bin/<configuration>/<framework>` and `obj/<configuration>/<framework>`
— and two things then go wrong, neither of them the designer's fault in the user's eyes:

- **The application runs from the IDE's output.** Its assembly is mapped by the process, and the
  next build that copies over it retries ten times and stops with `MSB3027`, "the file is locked by
  .NET Host" — true, unactionable, and looking like the designer is broken.
- **Two builds write one intermediate folder.** Rider saves the files before it runs; the designer
  hears the save and builds too; both write `obj/Debug/<framework>/App.dll`, and one of them fails —
  possibly the user's.

The first answer, in the designer sample, was a global `BaseOutputPath=bin/ArxisStudio/`. It moved
the output and broke the items. The SDK's default excludes are derived from the base paths
(`Microsoft.NET.Sdk.DefaultItems.targets`: `$(BaseOutputPath)/**` and `$(BaseIntermediateOutputPath)/**`
in `DefaultItemExcludes`), so with the base moved, the IDE's `bin/Debug/**` was no longer excluded and
every file of its output became a `None` item of the project. Moving `BaseIntermediateOutputPath` the
same way would have taken the IDE's generated `obj/Debug/**/*.cs` into `Compile`, and every assembly
attribute twice (`CS0579`).

## Decision

**A design build sets the output and intermediate paths literally, and leaves the bases alone.**
`MSBuildDesignOutput.GlobalProperties` is `OutputPath=bin/ArxisStudio/` and
`IntermediateOutputPath=obj/ArxisStudio/`, passed as global properties to the designer's evaluations
and its operations alike — the evaluation says where the build will put the assembly, and the two
would disagree if only one of them knew.

The bases stay `bin/` and `obj/`, so the default excludes keep both tools' output out of every glob,
and the restore is shared: `project.assets.json` and the `nuget.g.props` and `.targets` it writes stay
under `obj/`, where both tools read them. One restore serves both, and the IDE's restore keeps the
designer's evaluation current.

**A design build restores first only when it has to.** `ProjectSnapshot.RestoreOutputs` names the
evaluation inputs a restore writes — the assets file and the imports NuGet generates — and a host
restores before building when none of them is on disk, or when an input changed that is not among
them: the project file, an import somebody edits. Restore's own output changing is a restore having
run, and restoring again for it would loop — the IDE's restore would set off the designer's, whose
writes would set off another.

## Consequences

- **One framework per design build.** The SDK appends the framework to `OutputPath` by changing the
  property, which a global property does not allow, so two frameworks would write one folder. A
  multi-targeted project's design build names its framework, through the request's
  `TargetFramework`.
- **Referenced projects build into their own design folders.** Global properties flow to project
  references; the common targets remove `OutputPath` from them only when
  `PassOutputPathToReferencedProjects` is `false` (`_AddOutputPathToGlobalPropertiesToRemove` in
  `Microsoft.Common.CurrentVersion.targets`) and never remove `IntermediateOutputPath`, and both are
  relative, so each project resolves them against itself. `TargetFramework` is undefined for
  references and negotiated as usual.
- **Moved bases are not followed.** With an artifacts layout or bases moved by the project, the design
  output still lands in the project's own `bin/ArxisStudio` and `obj/ArxisStudio`, because a global
  property cannot name another property. `ProjectSnapshot.BuildDirectories` names them all the same,
  as folders outside the bases ([ADR 0025](0025-file-changes-carry-their-kind-and-the-snapshot-classifies-them.md)),
  so a design build is still not a project changing.
- **The designer's own application does not lock its next build** only if it runs from a copy: the
  host starts what it built from elsewhere. That is the host's half of this decision.
- **Tested by evaluation, proven by building.** The tests here evaluate with the design properties —
  what is globbed, where the output and the restore go — because the contract keeps restore, and so
  any SDK build, out of the tests. That a build lands where the evaluation says, and is not blocked by
  an application running from the IDE's output, is what the designer's stand checks with real builds.

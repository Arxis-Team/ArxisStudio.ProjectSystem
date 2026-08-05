# 12. Restore assets are read, not resolved

Date: 2026-08-05
Status: Accepted

## Context

A designer's central question is which assembly contains a type. For a type from a package, the
answer lives in `project.assets.json`: restore records the exact versions it chose, everything it
pulled in transitively, and which files each package contributes for each framework.

MSBuild evaluation does not provide this. Resolved references appear only after the
`ResolvePackageAssets` target runs, which is a build step rather than an evaluation, and Milestone 4
owns builds. So the assets file is read directly.

There is an obvious library for reading it: `NuGet.ProjectModel`, whose `LockFileFormat` returns a
typed `LockFile`. Using it would mean a `NuGet.*` package reference in the MSBuild provider.

The task specification is explicit that reading restore output is not package management: "merely
reading evaluated `PackageReference` items or resolved restore assets belongs to the MSBuild /
project-system side". So the *capability* belongs here. The question is only which code reads the
file.

## Decision

Read it by hand, with `System.Text.Json`. No NuGet client library enters any package in this family.

Three reasons, in the order they mattered.

**The guard is worth more than the convenience.** `ForbiddenDependencies` forbids `NuGet.*`
everywhere, and that rule is one of the few things standing between this design and the usual fate
of project systems, where every package eventually references everything. Relaxing it for a
convenience — reading about thirty lines' worth of a documented format — would spend a boundary on
something that does not need it. `ArxisStudio.ProjectSystem.NuGet` exists for when package
*management* is wanted, and that is where a NuGet dependency belongs.

**The part that matters is small and stable.** Four sections are used: `targets`, `libraries`,
`packageFolders` and `projectFileDependencyGroups`. Their shape has survived every format version so
far; versions add fields rather than rearrange these. The reader takes what it recognises and
ignores the rest, so an unfamiliar `version` is read rather than refused — failing on an unknown
number would break the day a new SDK ships, for no benefit.

**It made the testing question disappear.** [ADR 0010](0010-testing-a-provider-that-needs-a-real-engine.md)
had to reason carefully about depending on a real SDK, because evaluation needs an engine. Reading
JSON needs nothing: the test writes the file. So the whole reader is covered by cheap, exact cases
with no restore, no package cache and no network — which is what the contract demands and what a
`LockFile` API would have made harder, not easier, since a `LockFile` still has to come from
somewhere.

## Consequences

- **Two placeholder shapes must be recognised, and both were found in real data rather than
  guessed.** `_._` is restore's marker for "this package contributes nothing of that kind" — six of
  the ten entries in this repository's own assets file are that, because of `ExcludeAssets="runtime"`.
  And an entry of type `project` carries `bin/placeholder/Name.dll`, which never exists. Taken
  literally either becomes a path to a file that is not there. Project entries are skipped entirely,
  since a referenced project's real output comes from evaluating it.
- **Compile and runtime assemblies are kept apart** because they genuinely differ, which the same
  real data shows: a package compiled against a reference assembly can contribute nothing at run
  time. A consumer building a compiler invocation and one building a runtime environment need
  different answers.
- If NuGet changes the format incompatibly, this breaks and the reader is where to fix it. The
  alternative was a dependency that would have made a different day worse.
- A project that declares packages with no restore output gets `APS2005` as a **warning**. It is the
  ordinary state of a freshly cloned repository, and the snapshot is still useful — everything
  declared is present, only the resolution is missing.
- Nothing here checks whether the assets file is *stale* relative to the project. Detecting that is
  its own problem and belongs with the freshness work, not smuggled in here.

# 5. One path policy, and it is case-insensitive everywhere

Date: 2026-08-05
Status: Accepted

## Context

A project system is mostly paths. The specification asks for one documented normalisation and
comparison policy, and the reason is a specific bug: a solution file writes `src\App\App.csproj`
while a project reference writes `..\App\app.csproj`, and a model that treats those as two paths puts
one project into the graph twice. Everything downstream — the reference graph, the item lists, a
consumer's cache keyed by identity — is then wrong in a way that is very hard to diagnose from the
symptom.

Case is the contested part. Three options were considered.

**Follow the operating system.** Superficially obvious and wrong on both platforms: Windows has had
per-directory case sensitivity since 1803, and macOS is case-insensitive by default despite being
Unix. It also makes the meaning of a snapshot depend on where it was built, and makes half the
equality tests unrunnable on any given machine — the specification requires tests that do not depend
on machine specifics.

**Case-sensitive everywhere.** Deterministic, and wrong for the domain.

**Case-insensitive everywhere.** Deterministic, and matches the engine.

## Decision

`CanonicalPath` compares and hashes `OrdinalIgnoreCase` on every operating system. Casing is
preserved in `Value`; only comparison folds it. The comparer is one `internal static` field in
`CanonicalPathFormat`, so there is exactly one answer to "are these the same file" and one place to
change it.

The deciding argument is the domain: **MSBuild compares paths case-insensitively on every platform.**
Item deduplication, project-reference matching and solution-to-project matching all do. A core that
split what the engine populating it considers one project would be wrong about its own data.

The failure modes are also not symmetric. The false split is routine in real repositories and
corrupts the project graph. The false merge needs two files differing only in case in one directory
on a case-sensitive volume — a layout that already breaks MSBuild.

Normalisation, in order: reject null, blank and `\0`; put both separator characters onto the
platform's; reject anything not fully qualified — which catches relative paths and the drive-relative
`C:foo`, the one form that would otherwise resolve against a per-drive current directory; resolve
`.` and `..` with the **two-argument** `Path.GetFullPath`, whose base is the path's own root; strip
trailing separators except at a root.

Current-directory independence therefore holds by construction rather than by discipline. There are
two doors — already fully qualified, or combined with a `CanonicalPath` base — and neither consults
the process working directory. A test refuses the single-argument overload anywhere in `src/`.

No `Uri`, and no filesystem access at all. Both are enforced by the same source scan.

## Consequences

- One project cannot appear twice because of spelling, which is the bug this exists to prevent.
- `ProjectIdentity` inherits the policy for free, since it is derived from a canonical path.
- On a case-sensitive volume, two files differing only in case are one path to this library. Recorded
  in `docs/limitations.md`.
- A Unix file name containing a literal backslash cannot be represented, because `\` is normalised to
  a separator on every platform. This is the same trade MSBuild makes and the domain requires it.
  Recorded.
- `\\?\C:\x` and `C:\x` are distinct values. Collapsing them would be four lines and is deliberately
  not done: the prefix exists to bypass Win32 normalisation and can name files the normal form
  cannot, so merging them would occasionally change which file was meant. Recorded.
- The core touches no filesystem, so a `CanonicalPath` naming a file that does not exist is perfectly
  valid. Noticing that it is missing is a provider's job, reported as a diagnostic.

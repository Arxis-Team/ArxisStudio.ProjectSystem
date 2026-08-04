# Known limitations

The honest list of what `ArxisStudio.ProjectSystem` does not do, and of the costs its design
decisions carry. A limitation nobody wrote down is discovered one user at a time.

## Milestone 0

### No file-format provider ships

The core models solutions, projects, references, items, and outputs, but reads none of them. There
is no `.sln`, `.slnx`, or `.csproj` parsing in this package and no MSBuild evaluation. Until
`ArxisStudio.ProjectSystem.MSBuild` exists, a consumer must implement `IProjectSystemProvider`
itself to get any data into a workspace.

This is deliberate, not an oversight: the boundary is proved before anything is built on it.

### The core touches no filesystem

Paths are normalized lexically and compared as values. Nothing is opened, and no path is checked
for existence — including inside equality and hashing, where a filesystem call would make a
snapshot's meaning depend on the state of the disk at the moment it was compared.

A consequence worth stating: a `CanonicalPath` that names a file which does not exist is a
perfectly valid `CanonicalPath`. Reporting that as a problem is a provider's job.

### No coalescing of refreshes

Requests are serialized in arrival order and every one of them runs. Submitting *N* refreshes runs
*N* provider loads; none is skipped because a newer one is already queued.

Debouncing belongs with the file-change events that make it necessary, which is Milestone 5.
Adding it here would mean designing a coalescing policy against no real trigger, and the
specification is explicit that coalescing must be a deliberate design with deterministic tests
rather than an incidental race.

## Deferred by design

Not limitations of the implementation so much as scope boundaries, listed so nobody looks for
them: restore and build execution, file watching, assembly loading, `AssemblyLoadContext`
management, package-manager operations, project-file editing, and any UI.

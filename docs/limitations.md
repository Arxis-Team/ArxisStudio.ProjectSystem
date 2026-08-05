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

### Paths compare case-insensitively everywhere, including Linux

`CanonicalPath` folds case on every operating system. The reasoning is in the ADR and on the type,
and it is a genuine trade: on a case-sensitive volume, two files in one directory differing only in
case are one path to this library.

That layout already breaks MSBuild, so nothing correct is lost — but if you are building on this
and your users are on such volumes, this is the sentence to know about.

### A backslash cannot appear in a Unix file name

Normalisation replaces `\` with the platform separator on every platform, because MSBuild writes
`src\App\file.cs` into project files regardless of where the build runs, and reading that literally
on Linux would turn a project's whole item list into one absurd file name.

The cost is that a Unix file genuinely named `weird\name.cs` cannot be represented. This is the
same trade MSBuild itself makes.

### Extended-length Windows paths are distinct from their normal form

`\\?\C:\src\App.csproj` and `C:\src\App.csproj` are two different `CanonicalPath` values.
Collapsing them would be a few lines, and is deliberately not done: the `\\?\` prefix exists
precisely to bypass Win32 path normalisation, and can name files that the normal form cannot. Two
spellings that are *usually* the same file are not reliably the same file, and silently merging
them would occasionally change which one was meant.

### Notification delivery is not ordered

Publication is strictly ordered; **delivery is not**. `SnapshotChanged` is raised after the mutation
boundary is released — which is what lets a handler start another load without deadlocking — so the
next publication can happen while the previous notification is still being delivered. Two handlers
can be running for two versions at once, and nothing promises the older one finishes first.

A handler that keeps derived state must compare `WorkspaceChangedEventArgs.Version` against the
version it last saw and ignore anything older. That is what the version is for. Ordering delivery
would mean a second lock outside the gate, which introduces a deadlock a handler could reach by
awaiting a load; the version comparison is the cheaper and safer contract.

### A subscriber's failure is silent

`SnapshotChanged` isolates a throwing handler: the others still run and the exception never reaches
the caller of the load. The reasoning is in
[ADR 0007](adr/0007-a-throwing-subscriber-is-isolated.md), and the cost is stated plainly there —
nothing reports the failure. A handler that wants its own failures surfaced must catch them itself.

### No coalescing of refreshes

Requests are serialized in arrival order and every one of them runs. Submitting *N* refreshes runs
*N* provider loads; none is skipped because a newer one is already queued.

Debouncing belongs with the file-change events that make it necessary, which is Milestone 5.
Adding it here would mean designing a coalescing policy against no real trigger, and the
specification is explicit that coalescing must be a deliberate design with deterministic tests
rather than an incidental race.

## The MSBuild provider

### A solution's projects are evaluated one at a time

Correctness first. Each evaluation gets its own `ProjectCollection`, and nothing measured here yet
says that running several at once is safe alongside the SDK resolvers and caches MSBuild installs
per process. A large solution is therefore slower than it could be, and this is the first thing to
measure when that matters — not the first thing to guess at.

### A cross-targeting project is evaluated twice

Its outer evaluation says which frameworks exist and has no output path, so a second evaluation runs
inside one framework to produce a snapshot that belongs to an explicit context. When the request
names a framework, only that one evaluation happens.

The framework chosen when the caller does not name one is the first the project declares.
`ProjectSnapshot.ActiveTargetFramework` says which it was, and `TargetFrameworks` still lists them
all — so nothing is hidden, but a caller who cares should say which one it wants.

### Only the active framework's context is reported

Each framework of a cross-targeting project can resolve a different dependency graph. A snapshot
describes one of them: the references, items and outputs are the active framework's. Loading the
same project again with a different `TargetFramework` gives the other. Representing several at once
would need a shape nothing has asked for yet.

### A project whose SDK or imports cannot be resolved does not evaluate at all

MSBuild can be asked to ignore missing and invalid imports, and it was — until measuring showed that
it then suppresses the *report* as well as the failure, so a project with an unresolvable SDK
evaluated "successfully" while missing everything that SDK contributes. Those settings are off now,
so such a project fails with MSBuild's own explanation instead of returning a snapshot that looks
complete.

The cost is that no partial snapshot comes back for it. Inside a solution this is invisible: the
project stays in the snapshot carrying its diagnostic and the rest of the solution loads.

### Nothing checks whether restore output is stale

`ResolvedPackages` reports what the assets file says, and the assets file may be older than the
project that produced it. A consumer that changed a `PackageReference` and has not restored will get
the previous resolution without being told. Detecting staleness is its own problem — it needs
timestamps or hashes over the inputs — and it belongs with the freshness work rather than smuggled
into the reader.

### Resolved packages are the active framework's only

The assets file records a resolution per target framework, and a snapshot carries the one matching
its `ActiveTargetFramework`. A cross-targeting project resolves differently per framework; loading it
again with another `TargetFramework` gives that framework's resolution.

### Evaluation cannot be interrupted

MSBuild offers no way to abandon an evaluation part-way. The cancellation token is observed between
projects and after the work finishes, so cancelling a solution load stops it at the next project
boundary rather than immediately. The work runs on the thread pool, so the caller does get control
back at once, but a thread stays busy until the current project is done.

### Solution folders are a flat list with paths

Nesting is expressed by the path — `/src/Libraries/` sits inside `/src/` — rather than by parent and
child links. A consumer that wants a tree builds one from the paths. Modelling the links as well
would be two representations of one fact, and they would eventually disagree.

### Solution configurations are not mapped to project configurations

A solution says which project configuration each of its own configurations selects. That mapping is
read by nothing here: `SolutionSnapshot.Configurations` lists what the solution declares, and each
project reports what it was evaluated under. Joining the two is worth doing when something needs it,
and inventing the shape before then would be guessing.

### A project reference outside the solution has no identity

`ProjectReferenceInfo.Project` is resolved only when the target is one of the projects being loaded.
A reference pointing outside the solution keeps its `ProjectFilePath` and gets
`ProjectIdentity.None`, because an identity for a project nobody opened would be a handle to
nothing.

## Deferred by design

Not limitations of the implementation so much as scope boundaries, listed so nobody looks for
them: restore and build execution, file watching, assembly loading, `AssemblyLoadContext`
management, package-manager operations, project-file editing, and any UI.

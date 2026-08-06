# Known limitations

The honest list of what `ArxisStudio.ProjectSystem` does not do, and of the costs its design
decisions carry. A limitation nobody wrote down is discovered one user at a time.

## The core

### The core reads nothing

`ArxisStudio.ProjectSystem` models solutions, projects, references, items and outputs, and parses
none of them. There is no `.sln`, `.slnx` or `.csproj` reading in it and no MSBuild evaluation: a
consumer referencing the core alone supplies an `IProjectSystemProvider` of its own, or references
`ArxisStudio.ProjectSystem.MSBuild` for one that reads real projects.

This is the whole design rather than a gap — see
[ADR 0001](adr/0001-core-is-provider-neutral.md) — and it is what lets a snapshot stay readable
after the engine that produced it is gone.

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

### The convention walk is repeated for three files, and no further than the entry point

`EvaluationInputs` names the places a `Directory.Build.props`, a `Directory.Build.targets` and a
`Directory.Packages.props` would be looked for, whether or not one is there — every directory from
the project's own up to and including the one holding the file in use, because MSBuild stops at the
first it finds and a nearer file takes over from a further one.

Three limits follow, all deliberate.

**Only those three.** The SDK walks up for other things — an `.editorconfig`, a response file — and
those are read by the compiler or the command line rather than by evaluation. A project's evaluation
inputs are about what makes *this snapshot* untrue.

**No further than the entry point's directory** when MSBuild found nothing at all. Its own walk ends
at the root of the drive, and naming every directory up to `C:\` would ask a host to watch the
volume because a project three levels down has no `Directory.Build.props`. A file created above the
opened solution is therefore missed; a host that cares watches more.

**A project outside the entry point's tree gets only its own directory.** A solution may reference a
project anywhere, and for such a project the ceiling is not an ancestor — so rather than walk
towards somewhere it never was, the one place a new file certainly reaches it is named and no more.

Every other import is still reported only if it resolved. An SDK that conditionally imports a file
by a path of its own devising is not predicted.

### A project reference outside the solution has no identity

`ProjectReferenceInfo.Project` is resolved only when the target is one of the projects being loaded.
A reference pointing outside the solution keeps its `ProjectFilePath` and gets
`ProjectIdentity.None`, because an identity for a project nobody opened would be a handle to
nothing.

## Restore, build, rebuild and clean

### A build changes nothing the workspace knows

Running one publishes no snapshot and does not advance the version, because nothing was re-read —
the reasoning is in [ADR 0014](adr/0014-an-operation-is-not-a-mutation.md). A caller that wants the
model to reflect what a build produced calls `RefreshAsync` itself, at a moment it chose.

The cost is that this is not automatic, and a caller who forgets will read a snapshot describing the
project as it was before. That is the deliberate trade for a version whose increment always means
the same thing.

### One build at a time, per process

`BuildManager.DefaultBuildManager` is a singleton and the MSBuild provider serialises builds behind
its own lock. Two concurrent `ExecuteAsync` calls run one after the other, and a host that uses the
default build manager for its own purposes is sharing that singleton with this library.

Parallelism across projects still happens — MSBuild's own scheduler provides it *inside* one build.
What cannot happen is two builds at once.

### Disposal does not wait for a running operation

`DisposeAsync` waits for the mutation boundary, and an operation never takes it. So a workspace
disposed while a build is running returns immediately and the build finishes on its own. There is no
workspace state for it to corrupt, but a host that needs the process to be quiet before exiting must
track its own operations.

### A cancelled build may still have done some of the work

Cancellation reaches MSBuild, which abandons its submissions, but a target that had already run has
already run. Files written before the cancellation stay written. `OperationCanceledException` means
"this was stopped", not "nothing happened" — a caller wanting a known state after cancelling should
clean.

### Nothing checks whether build output is stale

The same gap as restore output, one level up. `Outputs` describes where a build puts things, not
whether what is there now came from the current source, and nothing reports a timestamp or a version
identity for it either. Both are deliberate: baking "this file was current at 14:02" into an
immutable snapshot produces a value that starts ageing the moment it is taken and cannot say so. A
consumer holds the paths and can ask the file system whenever the answer needs to be fresh.

Detecting staleness properly needs to know when inputs change, which is the freshness work.

### The symbol file's path is composed, not read

Every other artifact comes from a property that names it. Symbols have none: the SDK builds the path
inside a target, from the same output directory and assembly name that make up `TargetPath`, so this
composes it the same way and gates it on `DebugType`.

A project that redirects its symbols somewhere else therefore gets a `SymbolFile` path that is
wrong. It is reported anyway because a consumer mapping a stack frame to source degrades gracefully
when the file is not there, and reporting nothing helps nobody.

## Watching

### Nothing refreshes on its own

The workspace does not watch files, and a change never publishes a snapshot by itself. Watching is
four pieces a host composes — evaluation inputs, a watcher, a coalescer, an invalidation — and the
`RefreshAsync` at the end is the host's call. [ADR 0016](adr/0016-watching-belongs-with-the-provider.md)
says why: a refresh started from a timer callback has no caller to report a failure to.

### The watcher reports files nothing cares about

It watches directories rather than individual files, so every change in a watched directory is
reported and `Invalidate` discards most of them. That is deliberate — a watcher bound to one file
goes deaf when the file is replaced rather than edited — but it means the callback is noisier than
the set of files that were asked for, and noisiest for a project whose `obj` does not exist yet,
where the whole project directory is watched recursively until it does.

### A notification buffer overflow costs a full re-read

When the operating system's buffer overflows — a branch switch will do it — the changes are lost and
unknowable. Every watched path is then reported as changed, which is the only answer that cannot
silently miss one, and the result is that everything looks stale at once.

### File observation is per-path, not per-change-kind

The watcher reports paths. Whether a file was created, edited, renamed or deleted is not passed on,
because nothing here needs it: any of them makes a project stale in exactly the same way. A host
that wants to distinguish them uses its own file observation, which it probably already has.

## Package management

### Uninstalling leaves the central version behind

Under central package management, removing a `PackageReference` from a project does not remove the
`PackageVersion` from `Directory.Packages.props`. That file is shared by every project in the
repository and the editor is given one project, so it cannot see whether anything else still needs
the pin. Removing it would break projects it never looked at.

The cost is an entry that may now be unused. A tool that wants to tidy those up has to look at every
project, which is a different operation over a different input.

### Nothing restores, and the editor does not consult a feed

The editor writes what it is told to write. It does not check that the version it was given exists —
a version string is written verbatim, because `[1.0,2.0)` and `1.0.0-*` are both meaningful and a
library that normalised them would change what the project asked for. Searching and choosing are
separate calls a caller makes first.

Restoring afterwards is the caller's, through the workspace's own operations. That keeps the rule
that changing disk is not changing the model.

### The bundled feed reads no configuration and cannot authenticate

`NuGetHttpFeed` speaks enough NuGet V3 to search a public feed and list a package's versions. It does
**not** read `NuGet.config`, so it does not discover configured sources, and it does not implement
NuGet's credential-provider model, so it cannot reach a private feed.

Both are deliberate rather than unfinished. Source discovery is hierarchical and credentials are a
plugin protocol; a half-implementation of either fails by silently not finding a package, which is
worse than not offering the feature. A host with private sources implements `IPackageFeed` over its
own client, which is why that interface exists.

### A search reflects one feed, not a repository's real source list

Because sources are not discovered, whatever feed a host constructs is the only one asked. A project
restored from several sources will not have all of them represented in a search, and package source
mapping is not applied.

### Nothing says what a package will drag in before you install it

Transitive dependencies are reported *after* a restore, from the assets file, by the MSBuild
provider. Nothing here answers "what will installing this pull in" beforehand, because that is
dependency resolution — NuGet's resolver, not a feed query — and the package deliberately does not
host one.

Deprecation, advisories and licence metadata *are* read, by `IPackageFeed.GetMetadataAsync`, and
come with two limits worth knowing. They are read from the SemVer 2.0.0 registration resource
(`RegistrationsBaseUrl/3.6.0`) and that resource only — measured on nuget.org, where a package shows
32 deprecated versions and 28 advisories under it and none at all under the unversioned
`RegistrationsBaseUrl`. A feed advertising only the latter is refused with a diagnostic rather than
read, because reading it would report a vulnerable package as clean.

And the answer is per version, as the feed publishes it. Nothing here decides whether the advisory
applies to *your* use of the package, and nothing walks the dependency graph: a vulnerability in a
transitive dependency is not reported against the package that pulled it in.

### An install undoes its edit, but a restore is not itself transactional

When a restore fails after a change, the project files are put back. What the restore already did to
`obj/` and to the package cache is left as it is: those are NuGet's to manage and re-restoring is
what fixes them. So the project returns to its previous state and the intermediate output may not
match it until the next restore.

If the undo itself fails — the file became read-only, or is now held open — that is `APS4009`, and
the project is left carrying a reference that does not restore. Nothing can be done about it from
here except say so loudly.

### An edit is transactional, not durable

If two files must change and the second write fails, the first is put back from bytes held in
memory. That handles the failure that actually happens — a file open in another editor. It is not a
journal: a process killed mid-write leaves what it left, and nothing defends against another process
writing the same file at the same moment.

## The Markup adapter

### It needs the Markup repository beside this one

`ArxisStudio.ProjectSystem.Markup.Xaml` references `ArxisStudio.Markup.Xaml.Loader` by a relative
path into a sibling checkout, for the reasons in
[ADR 0018](adr/0018-the-adapter-references-markup-by-source.md). Build it without that repository
present and it fails with `APSADAPTER01` saying exactly what is missing. Nothing else in this
repository has that requirement.

### Unloading is asked for, not guaranteed

`ProjectAssemblyContext.Dispose` unloads its collectible context, and the runtime frees it only once
nothing refers to anything inside. A host still showing a control built from those assemblies keeps
them alive, which is correct — so disposal returns immediately rather than waiting, and a host that
leaks references leaks assemblies.

### Package assemblies are loaded into the default context, and stay

Only what gets rebuilt — the project's own output and its referenced projects' — goes into the
collectible context. Package assemblies go to the default one, because they do not change between
builds and because a second copy of a shared library produces types that are not assignable to the
host's. The consequence is that changing a package version needs a new process, not a new context.

### Only `AvaloniaXaml` and `AvaloniaResource` items get an `avares` URI

Those are the two item types the Avalonia SDK embeds: the first is what it makes of a `.axaml`
document, the second of an asset. A project that arranges its resources some other way resolves
nothing from the project, and falls through to whatever the built assemblies contain.

Exposing every file under an `avares` URI would be worse than resolving none: the designer would
then answer a question the running application answers differently, which is the one failure mode a
designer must not have.

### A resource of a project that has never been loaded is not found

The map covers the projects in the snapshot. A `ResourceInclude` naming a project outside it — a
solution opened on one project, referencing another by path — resolves out of the built assembly if
there is one, and otherwise not at all.

### A translated diagnostic keeps its position only when the text is supplied

Markup measures a span as an offset and a length into the document; the project model measures it as
lines and columns, because that is what a compiler and MSBuild report. Neither converts into the
other without the text, so `ProjectMarkupDiagnostics` asks for it — and translates without a position
rather than with a wrong one when it is not given. A diagnostic pointing at the wrong line is worse
than one pointing nowhere, because somebody will act on it.

An `avares` document URI yields no file path either. It addresses a resource inside an assembly, and
which file it came from is a question only a project model can answer.

### No root-instance factory is supplied

A generated `x:Class` partial usually calls `InitializeComponent()` from its constructor, so
constructing one and then populating it loads the document twice. Markup's own default says as much
and offers `IXamlRootInstanceFactory` for a caller that knows better.

This adapter does not implement one, because it knows no better: whether a constructor initialises
itself is a fact about code neither library has seen, and a factory here would be guessing on the
host's behalf. A host that controls the generated types supplies one.

### Assemblies are matched by file name

The map from an assembly name to a file is built from the file names in the snapshot. For anything a
build produces that is exactly the assembly's simple name, and packages lay their files out the same
way — but a file whose contents disagree with its name resolves under the name on disk.

## Deferred by design

Not limitations of the implementation so much as scope boundaries, listed so nobody looks for them.

- **Any user interface.** These are libraries a tool is built on, and none of them draws anything.
- **Parsing or rewriting AXAML.** That is `ArxisStudio.Markup`'s work, and the boundary is the point
  of having two families. This side may say which files a project contains; it never looks inside
  one.
- **Hosting a user's controls in this process.** The adapter loads assemblies so a host can build a
  preview; deciding to run somebody's code, and containing it when it misbehaves, belongs to the
  host that has a window to put it in.
- **Publishing.** These are referenced by source, not packed to a feed —
  [ADR 0011](adr/0011-these-libraries-are-referenced-not-published.md). There is no versioning
  ceremony, no release process and no `dotnet pack` output.
- **Benchmarks.** Nothing here has been measured for speed, and the correctness-first choices say so
  where they were made.

There are two samples. `samples/FormsDesigner` is a visual form designer over all three families at
once — `ArxisStudio.DesignEditor` for the surface, `ArxisStudio.Markup` for the document, this one
for the project — and it needs both sibling repositories checked out beside this one. Its own README
lists what it does not do; the one worth repeating here is that a form whose `x:Class` names a type
the project has not compiled cannot be shown at all, which is a fact about loading a document rather
than about this designer.

### A bare `using:` finds the projects, not the packages

`ProjectXamlEnvironment` hands the type resolver the assemblies the projects *build* — the project's
own output and its referenced projects' — and not the restored packages. That is what makes
`x:Class` and a locally defined control resolve, since both are written as a bare `using:` with no
assembly named.

A type in a package named the same way is not found unless the host has already loaded that package
for its own reasons. Write `clr-namespace:Ns;assembly=ThePackage` for it, which resolves by name and
always worked. Searching the packages too would mean loading every restored assembly before anything
asked for one, into the default context where they stay for the life of the process.

`samples/ProjectSystem.Ide` is the other, and it is the one that earned its place twice over: it
uses every package at once and found a real defect in the adapter within minutes of first running —
the resource map accepted only `AvaloniaResource`, so it saw none of the `.axaml` documents in a
real Avalonia project. No unit test had noticed, because every fixture used the item type the code
happened to accept.

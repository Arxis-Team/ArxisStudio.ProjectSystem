# 0028. The design host lives in the adapter and replaces a generation in order

Date: 2026-10-03

Status: Accepted. Builds on [0016](0016-watching-belongs-with-the-provider.md),
[0023](0023-a-generation-is-reclaimed-before-its-successor-is-born.md),
[0025](0025-file-changes-carry-their-kind-and-the-snapshot-classifies-them.md),
[0026](0026-a-design-build-writes-beside-the-ides-never-over-it.md) and
[0027](0027-a-design-set-is-one-generation.md).

## Context

Everything a designer beside an IDE needs from this family existed as primitives: a generation of
the design set and its reclaim, an environment per project, the population that makes placed
controls follow their documents, classified file changes, design builds that never write over the
IDE's. The orchestration between them — which change means what, when to build, when to replace the
types, in what order to let go, what to do when a generation stays — lived in the one designer that
used them, `UiDesigner.Demo`, in about a thousand lines spread over six files. Measured on that
designer, it got the hard parts wrong in ways no primitive could prevent:

- **The swap waited for the window to be in front and for the application to stop.** Neither holds
  the designer's types; both left the person looking at yesterday's form for as long as the other
  editor had focus.
- **The order of letting go was the caller's to get right**, and getting it wrong is silent: a
  participant still holding a root, a window a replaced session built and nobody closed, a population
  disposed after the generation it holds, and the reclaim honestly answers that the generation
  stayed — a restart instead of a swap, every time, for a reason no log names.
- **An open landed between two generations.** A form opened while the types were being replaced
  attached to whichever generation its environment happened to read.
- **Every save of a class built every project that owns an open form**, rather than the projects
  that build the change.

A second designer — the studio's own — would have had to rediscover each of these.

## Decision

**`ProjectDesignHost` in the adapter.** It is the one package allowed near Markup and Avalonia
([0018](0018-the-adapter-references-markup-by-source.md)), and the orchestration needs both: live
documents, sessions, windows to close, the dispatcher. The host does not watch — its owner feeds it
the coalescer's batches (`NotifyChanged`), as 0016 has every host compose watching.

**Classification decides, the host only applies it.** A batch is classified against the snapshot it
arrived against (0025): a renamed form follows its file (`RetargetAsync`), a deleted one is reported
and stays open until its owner closes it, a saved form goes into its document as a step of its
history — or, over unsaved edits, into a conflict nothing resolves silently — and into the placed
copies of its control; a form nobody has open still feeds those. The project is evaluated again only
when an evaluation input changed or a file came or went where a project's globs reach.

**Builds are the designer's own, of the top projects, after a quiet moment.** Saved code is built
once the code has been quiet for `BuildDelay` (400 ms; an editor's save-all is one build). What is
built is the *top* of what a change affects — the projects of the design set that changed or
reference one that did, less those another affected project builds: building the application builds
the library and recompiles what uses it, while building the library alone would leave the
application's code compiled against members that may be gone. One build at a time; a change during a
build is built after it. A restore runs first when a project was never restored or something its
restore reads changed — and never because a restore wrote its own output, which would loop (0026).
The host starts by building what is older than its sources, so a designer opened after the IDE moved
on does not show what the IDE built yesterday.

**Two things make a generation stale**: a build rewrote what it was loaded from (`IsCurrentOnDisk`),
or the design set or what it references changed. A third is not staleness but an end: a package the
generation loaded moved to another file — another version — and a package loaded into the default
context stays for the life of the process.

**A swap waits only for the designer.** Whatever is in the middle of something a swap would cut — a
gesture, a value half typed, a dialog, a drag — holds it off through `Gate.Defer`, and the swap runs
when the last deferral lets go and no build is pending. Nothing else holds it off: not focus, not the
running application.

**The order of a swap is the host's**, because every step of it was measured to matter:

1. participants let go (`IProjectDesignParticipant.ReleaseAsync` — the canvas freezes its last frame,
   the selection and the inspector drop what they hold), on the user interface thread, in the order
   they registered;
2. every document is detached — text, history and unsaved edits stay — and the windows their
   sessions built are closed: the host closes every window a replaced session built, on any
   replacement, because the windowing platform keeps a window nobody closed;
3. the host lets go of the population, the environments and the member resolver, from a synchronous
   method that is not inlined, so no local of the swap holds them;
4. the designer's own state goes last (`ReleaseHostState` — focus, a third-party library's statics),
   because letting go of the rest moves focus and routes commands;
5. one turn of the dispatcher at background priority, then the reclaim (0023);
6. reclaimed: the successor is built if out of date, loaded, registered, the documents somebody is
   looking at are attached to it (`SetVisibleDocuments`; the rest when shown, `EnsureLiveAsync`), and
   the participants take it up. Not reclaimed: no successor — `RestartRequired`, documents detached.

The swap takes the host's turn, so an open during a swap waits for the successor, and holds the build
lock, so no build writes what the successor is loading. `SwapCompleted` is raised once the host is
out of the swap, so a subscriber reads the state the swap left. No generation is created beside
another of the same assemblies, whoever retired it, and a disposed host reclaims its own so that the
next one finds nothing to wait for ([0029](0029-a-held-generation-may-be-asked-again.md)).

**Only a new process answers what a swap cannot**, and `ProjectDesignRestartReason` says which:
`GenerationStillHeld` — the generation, or a predecessor of its assemblies, stayed; `PackagesChanged`
— a package it loaded moved. The host keeps what it has: after a failed reclaim the documents stay
detached with their text, history and unsaved edits, the participants keep the frame they froze, and
no successor is made; after a moved package the generation stays live, because it still shows
everything but that package's new version. The host does not ask a held generation again — a
designer whose types would not go restarts.

**The session crosses a restart as text.** What a restart carries — the documents, their unsaved
text, what their files held — is the designer's to write and read back; a document is reopened with
`ProjectDesignDocumentOptions.Text` and `SavedText`, and `ReloadAsync` then compares it with its file.
The other editor goes on working while the designer restarts, and a file that moved on under
restored edits is a conflict, never a silent overwrite. The undo history stays with the process
that had it.

**Events on the user interface thread, each subscriber isolated** (0007); the work between them runs
off it, with every await `ConfigureAwait(false)` and every user interface step an explicit dispatcher
call.

## Consequences

- A designer is a thin host: it opens documents through the host, shows what their sessions build,
  registers its parts as participants, defers the swap around its gestures, and decides what to say
  about a conflict, a deleted file and a restart. `UiDesigner.Demo` is converted to it, and the
  studio integrates the same object.
- A designer's part that holds anything of a generation and does not register is a restart. That is
  the cost of a host that cannot see into a designer, and the reclaim names it honestly.
- A hidden document is detached during a swap and attached when shown, so a swap costs what is on
  screen rather than what is open.
- Two hosts over one project in one process cannot both have a generation (ADR 0029); the host is one
  per project per process.
- The host is tested over a provider that is a bench rather than MSBuild — it answers loads with a
  fixtures project and records the restores and builds it is asked for, a build moving the output's
  write time as a compiler's write would — with the build delay on a fake clock
  ([0030](0030-a-reclaim-is-tested-against-real-avalonia.md)).

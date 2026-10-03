# 0032. A design host reads, builds and saves through its owner

Date: 2026-10-03

Status: Accepted. Extends [0028](0028-the-design-host-replaces-a-generation-in-order.md).

## Context

`ProjectDesignHost` was written for a designer that owns its project model: it took a
`ProjectWorkspace`, read its snapshot, refreshed it after a restore, ran restores and builds through it,
and wrote a saved document's file itself with a `FileStream`. `UiDesigner.Demo` is that designer.

ArxisStudio is the other kind. Its project service owns the solution: one MSBuild workspace, evaluated
and built on one queue, so that two builds never write the same `obj/project.assets.json` at once. And
it owns the solution's files: one service writes them, records each write in a local history, and knows
its own writes from other editors' when its watcher reports them. A design host that needs its own
workspace there is a second owner of MSBuild racing the first, and a design host that writes its own
files is a save the local history records as "changed outside the studio".

## Decision

**The host reads and builds through a source.** `IProjectDesignSource` is the four things the host
used of a workspace: the newest snapshot, a notification that it moved, a refresh, and running an
operation. The snapshot carries the request it was evaluated with, so the host takes the operation's
configuration, platform, framework and global properties from it — the source publishes the two
together — rather than asking for the request separately. The notification carries nothing: the host
always read the newest snapshot rather than the one an event brought, because notifications may arrive
out of order.

`ProjectDesignSource.From(workspace)` is the workspace as a source, and the constructor over a workspace
is the constructor over that source. Its handler of the source's notification is a handler of the
workspace's own event — removing it removes it there — so a source holds nothing that outlives its
subscribers.

What a source promises is what the host relied on of a workspace, now written down: the snapshot is
evaluated with the host's build properties among its global properties and with the properties it reads
surfaced; an operation runs as requested, with no restore the host did not ask for; a refresh has
published or given up by the time it completes; failure is a result.

**The host saves through a writer.** `ProjectDesignHostOptions.Writer`, an `IProjectDesignWriter`,
writes a document's text to its file; without one the host writes in place, as before. The text carries
the encoding and byte-order mark it was read with. A writer that throws leaves the document unsaved: the
host marks a document saved only after the write returned.

Reading stays the host's. A file is read when a document opens, when it is reloaded and when a control's
document is registered, and none of those is anything an owner needs to record or vet.

## Consequences

- ArxisStudio's XAML service runs the host over its project service: a design profile of the solution
  evaluated with the design build properties on the service's own queue, and its file service as the
  writer. One owner of MSBuild, one writer of files, one local history.
- A source is free to evaluate the design profile however it keeps it — a second workspace of its own,
  as long as its operations queue with the IDE's.
- The workspace constructor stays and means what it meant; no existing host changes.
- A refresh the host asks for after a restore, or after files changed, goes to the owner, who may
  coalesce it with a reload of its own already pending.

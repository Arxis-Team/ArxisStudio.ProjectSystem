# 17. Project files are edited as XML, by the package that does not evaluate them

Date: 2026-08-05
Status: Accepted

## Context

Milestone 6 has to change project files: add a `PackageReference`, move a version, take one away.

There is an obvious tool for that. MSBuild's `ProjectRootElement` models project XML properly,
knows about item groups and conditions, and can save. Every IDE that edits project files uses it.
Taking it would mean a `Microsoft.Build` reference in `ArxisStudio.ProjectSystem.NuGet`.

The other question is what "correct" means for an edit. A project file is not a data file: it is
something a person wrote, keeps reading, and reviews the diffs of. An edit that is semantically
perfect and reformats the file is a bad edit, because the diff it produces cannot be reviewed and
the reviewer's only option is to trust it.

## Decision

**Edit the XML directly, with `XDocument` and `LoadOptions.PreserveWhitespace`, and take no MSBuild
reference.**

Each package in this family hosts one engine and no other. The MSBuild provider reads projects; the
package manager writes them; neither does the other's job. `ForbiddenDependencies` now states that
per package rather than as "everything except the core", because the rule worth enforcing is not
that the core is special — it is that a package hosting one engine must not quietly acquire a
second. Two packages that could both read a project file would eventually disagree about one.

The edits themselves are surgical. Nothing is regenerated: existing nodes keep their exact text,
new ones copy the indentation and the newline of what they are placed beside, and a group added at
the end reuses the whitespace already before `</Project>` rather than adding to it.

Two smaller decisions fall out and are worth stating:

**A new item is sorted only into a list that is already sorted.** Imposing alphabetical order on a
list somebody grouped by meaning rearranges their file as a side effect of adding one line; appending
to a list they were keeping sorted undoes their work. Copying whichever convention the file already
follows is the only option that makes no decision on their behalf.

**Uninstalling does not remove the central version.** Under central package management the version
lives in a file shared by every project in the repository, and this editor is given one project.
Removing the `PackageVersion` would break the projects it cannot see.

## Consequences

- **The interesting half is pure.** Rewriting is a function over an `XDocument`, so every case is
  tested with no file, no evaluation and no network — which is most of what could go wrong.
- **Formatting bugs are real bugs here and were found by testing for them.** Saving to a
  `StringWriter` makes LINQ to XML announce the writer's encoding, so every project file touched
  would have gained `<?xml version="1.0" encoding="utf-16"?>`. Whitespace outside the root element
  is a preserved node, so "adding the trailing newline back" doubled it and made every untouched
  file compare as changed. Both were caught by asserting on whole files rather than on fragments.
- **The edit is transactional across files.** Central package management splits one change over two
  files, and a half-applied edit leaves a repository that does not restore — a reference with no
  version. If the second write fails, the first is put back from bytes already in memory.
- **It is a transaction, not a database.** No journal, no crash recovery, no defence against another
  process writing the same file at the same moment. It handles the failure that actually happens: a
  file open in another editor.
- **Nothing restores.** Changing a project file changes what is on disk, not what the workspace
  knows, so it publishes nothing and does not advance the version — the same rule as
  [ADR 0014](0014-an-operation-is-not-a-mutation.md). A caller restores through the workspace and
  refreshes when it wants the model to catch up.
- If a future need genuinely requires MSBuild's understanding of conditions and imports to place an
  edit correctly, this is the decision to revisit — and it would mean moving the editing to the
  provider rather than adding an engine here.

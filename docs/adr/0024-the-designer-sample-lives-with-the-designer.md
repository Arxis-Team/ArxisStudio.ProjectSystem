# 0024. The designer sample lives with the designer

Date: 2026-09-30

Status: Accepted. Amends [0023](0023-a-generation-is-reclaimed-before-its-successor-is-born.md) in
one respect: where its harness is.

## Context

This repository carried two samples. `samples/ProjectSystem.Ide` shows what the model knows.
`samples/FormsDesigner` was a visual form designer over three families at once —
`ArxisStudio.Surface` for the canvas, `ArxisStudio.Markup` for the document, this one for the
project — and it grew to sixteen thousand lines, much of it about a canvas, a selection, an
inspector and a toolbox.

That put a designer in the one family that, by its own account, draws nothing
(`docs/limitations.md`, "Any user interface"). It also pointed the dependency the wrong way round
for a reader: the sample that shows what `ArxisStudio.Surface` is for lived in a repository that
library knows nothing about, while that library's own demo showed a designer with no document and no
project behind it. And it made this solution need a third repository beside it — Surface, in
addition to the Markup the adapter already requires — for the sake of a sample.

## Decision

**There is no designer in this repository.** The form designer moved to
`ArxisStudio.Surface/samples/UiDesigner.Demo`, where it replaced that library's previous demo, and
it references this repository's projects from there by relative path, the way the adapter
references Markup ([ADR 0018](0018-the-adapter-references-markup-by-source.md)).

What stays here is `samples/ProjectSystem.Ide`: a window that shows the model, not a tool that edits
forms. A sample added to this repository shows this family's packages; one whose subject is a
canvas, a document editor or any other sibling's library belongs with that sibling.

## Consequences

- This solution builds with `ArxisStudio.Markup` beside it and nothing else. Surface is no longer
  required.
- The harnesses the earlier decisions lean on did not stop existing; they changed address. The
  end-to-end check (`--verify`), the endurance cycle (`--stress`) and the generation-reclaim
  measurement (`--reclaim`, which [ADR 0023](0023-a-generation-is-reclaimed-before-its-successor-is-born.md)
  calls a permanent harness step) are switches of `UiDesigner.Demo` in ArxisStudio.Surface. "The
  sample" and "the studio" in ADRs 0021–0023 and in `docs/limitations.md` mean that program.
- A change to the adapter that those harnesses cover is verified from the sibling repository, which
  builds this one's projects by source. Nothing in this repository's `dotnet test` runs them, and
  nothing did before: they need a window and a real build.
- `Avalonia.Controls.ColorPicker` and `Avalonia.AvaloniaEdit` left `Directory.Packages.props` with
  the sample that used them.
- The sample's history stays in this repository's log, up to the commit that removed it. It was not
  carried across.

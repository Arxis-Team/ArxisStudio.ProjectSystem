# 0034. A project on another Avalonia is not loaded

Date: 2026-10-03

Status: Accepted. Extends [0028](0028-the-design-host-replaces-a-generation-in-order.md).

## Context

A generation loads the project's build into the designer's process, and the process has one Avalonia:
its own (ADR 0027 — everything the host has answers for its name). A project built against Avalonia 11
runs there against Avalonia 12. Its compiled markup and its code call members by signature, and a member
the process does not have fails at the call — not at loading, but one form, one property, one template at
a time, as the designer happens to show them. The designer looks broken in ways that say nothing about
the cause.

`UiDesigner.Demo` never met this: its projects build against the repository's own Avalonia. A designer
inside an IDE opens whatever solution a person has.

## Decision

**The host measures the design set's Avalonia against the process's before it makes a generation** — on
start, on every swap's successor, and on every snapshot while it is not making one. The version is the
one restore resolved for the package `Avalonia` (`ResolvedPackages`), compared with the version of the
Avalonia assembly the process runs:

- **Another major version** — `APS5003 AvaloniaVersionUnsupported`, an error. No generation is made and
  nothing is built for the design set: the state is `Unsupported`, `UnsupportedReason` says which project
  is built against which version and which the designer runs. Documents open with their text and nothing
  built from it, as they do while a restart is required.
- **Another minor version** — `APS5004 AvaloniaVersionDiffers`, a warning. The generation is made; the
  forms may differ from what the program shows.
- **Another patch, or nothing resolved yet** — nothing is said. A project restore has not answered for is
  measured once it has.

The measurement is reported with what the generation could not load (`GenerationDiagnostics`).

**It lifts by itself.** Unlike a restart, `Unsupported` is a state of the snapshot, not of the process:
nothing of the project was loaded. Each snapshot is measured again; while the version is still another
major one nothing moves — no swap is started for it — and the snapshot naming a version the process runs
marks the absent generation stale, and the swap that follows makes it and attaches the documents.

## Consequences

- A person who opens a solution on another Avalonia is told why its forms do not show, in one sentence,
  instead of meeting a hundred member failures.
- Updating the project's packages and restoring brings the designer back without restarting it.
- Projects that resolve no Avalonia — not restored, or not Avalonia projects — are not measured, and are
  designed as before.
- A form built against the same major version and another minor one is shown with a warning; whether it
  shows right depends on what that minor changed, which only the person can judge.

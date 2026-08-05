# 13. A provider may keep its engine's diagnostic codes

Date: 2026-08-05
Status: Accepted

Extends the diagnostics policy recorded in `CLAUDE.md` and
[ADR 0008](0008-a-result-state-computed-not-declared.md).

## Context

The core reserves `APS1xxx` for itself and a range per package, and the rule has been that a code is
a stable contract a consumer routes on. Milestone 3 needed SDK and workload diagnostics, and those
are problems MSBuild already notices and already names: an unresolvable SDK is `MSB4236`, a
duplicate import is `MSB4011`, a missing workload is `NETSDK1147`. Those names are in the SDK's
documentation and in every search result a user will find.

Two ways to surface them:

- **Wrap.** Give each a code from `APS2xxx` and put MSBuild's code in the message.
- **Pass through.** Report MSBuild's code as the diagnostic's code.

## Decision

Pass through. A provider reports its engine's diagnostics with the engine's own codes.

Wrapping fails the policy it appears to serve. The rule is that a caller decides what a diagnostic
means from `Code` and never from `Message` — and a wrapped diagnostic would put the only
distinguishing information in the message, so every consumer that wanted to tell a missing workload
from a duplicate import would have to parse text. One generic code for "MSBuild said something" is
not a contract; it is the absence of one.

So there are two vocabularies, and the split is by **who noticed**:

- `APS2xxx` — the provider itself noticed: it could not find MSBuild (`APS2001`), the evaluation
  threw (`APS2002`), the project file is not there (`APS2003`), the solution could not be read
  (`APS2004`), restore has produced nothing (`APS2005`).
- Anything else — the engine noticed and named it. `ProviderName` says which engine.

`APS2002` remains the code for a failure that stopped the evaluation, because MSBuild raises those
as an exception rather than through its logging service; the engine's explanation is carried in the
message, where it is the useful part rather than the routing key.

## Consequences

- A consumer can route on `NETSDK1147` or `MSB4011` directly, which is what makes the diagnostic
  actionable.
- **Codes are no longer all `APS`-prefixed**, and nothing enumerates the possible set. That is
  inherent: the set is whatever the engine can raise, and it grows with the SDK. A consumer that
  wants only this library's own findings can filter on the prefix.
- `DiagnosticCatalogueTests` is unaffected. It checks the codes this repository *declares*, and
  engine codes are not declared here — they are passed on.
- This decision is what made the second mechanism worth having at all. Measured: with
  `IgnoreMissingImports` set, MSBuild suppresses the *report* as well as the failure, so a project
  with an unresolvable SDK evaluated "successfully" while missing everything that SDK contributes.
  Those settings are now off, and a problem the evaluation survives — which the exception path never
  sees — arrives through the logging listener instead.
- A future provider should follow the same rule: its own findings get its range, and its engine's
  findings keep their names.

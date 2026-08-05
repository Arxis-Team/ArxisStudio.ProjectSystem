# 9. Evaluation happens in this process

Date: 2026-08-05
Status: Accepted

## Context

The task specification for Milestone 1 asks to "define in-process versus worker-process lifetime".
Defining it is the requirement; implementing both is not.

The case for a worker process is real and was the reason
[ADR 0003](0003-provider-types-do-not-cross-the-boundary.md) exists. MSBuild carries process-global
state: the locator installs an assembly resolver, there is one registration per process, and a
wedged evaluation cannot be abandoned. A worker isolates all of that and can be killed.

The case against doing it now is that it is a milestone of its own. Out-of-process evaluation needs
a serialization format for the whole model, a host protocol, process lifetime and crash handling,
and a story for how a worker finds its own SDK. None of that is project-system work; all of it has
to be right before the first `.csproj` opens.

## Decision

Evaluation runs in the calling process. `MSBuildProjectProvider` registers MSBuild through
`MSBuildEnvironment`, evaluates on the thread pool, copies the result into core-owned values, and
disposes the `ProjectCollection` before returning.

The seam that a worker would need is kept clean and is already load-bearing:

- Nothing MSBuild-shaped reaches the model. `PublicSurfaceTests.TheSnapshotModel_CarriesOnlyData`
  walks the model and refuses anything that would not survive a process boundary, and it runs
  against the provider as well as the core.
- The provider's whole contract is `IProjectSystemProvider`: a neutral request in, a neutral result
  out, no callback into the workspace, nothing that has to be alive.
- `EvaluatedProject` already separates "what MSBuild said" from "what we do with it", so a worker
  would serialize that and the translation would not move.

So moving evaluation into a worker later is a change of transport. No consumer-facing type changes,
and `MSBuildProjectProviderTests` would keep passing against a worker-backed provider.

## Consequences

- **MSBuild's global state is the host's problem.** One MSBuild per process, chosen by whoever
  registers first. `MSBuildEnvironment` documents this and adopts an existing registration rather
  than fighting it, because there is one assembly resolver per process either way.
- **A wedged evaluation cannot be recovered.** MSBuild offers no way to abandon one, so the token is
  observed before the work starts and after it finishes; a cancellation during a slow evaluation
  takes effect when that evaluation ends. The work runs on the thread pool, so the caller does get
  control back immediately — but the thread is still occupied. Recorded in `docs/limitations.md`.
- **A project that crashes the evaluator crashes the host.** Untrusted projects are already out of
  scope for exactly this reason, and the README says so. Opening untrusted projects needs the trust
  and isolation design that a worker would be part of.
- The `ProjectCollection` is created and disposed per evaluation rather than shared. A shared one
  would share its caches and its lifetime, and a stale cached project is a wrong answer that looks
  right. The cost is that nothing is reused between loads, which is the correct trade until there is
  a measurement saying otherwise.
- If a worker is wanted, the ADR that introduces it supersedes this one and should say what changed
  in the meantime — most likely a real consumer with a real reason.

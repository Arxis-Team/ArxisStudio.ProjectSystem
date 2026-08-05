# 10. Testing a provider that needs a real engine

Date: 2026-08-05
Status: Accepted

## Context

The specification says tests "must not depend on an installed IDE, Visual Studio, MSBuild workload,
NuGet cache, network access, or machine-specific paths". Milestone 1 is MSBuild evaluation. Read
literally and applied to the whole package, those two statements cannot both hold.

The tension is real but it is narrower than it looks. What the rule protects against is a test suite
that passes on the author's machine and fails on a colleague's, or in CI, because of something
installed. The .NET SDK is not that: it is already required to build this repository, `global.json`
pins which one, and CI installs it from that file. A test that uses the same toolchain the build
just used is not depending on a machine's accidents.

What *would* be that: a NuGet restore reaching the network or a warm package cache, a project that
needs an optional workload, or a path spelled `C:\Users\...`.

## Decision

Two layers, with the weight deliberately on the first.

**The translation is tested exhaustively and without MSBuild.** `EvaluatedProject` is a neutral
shape a test builds by hand, so every mapping rule — which property becomes which field, how a
cross-targeting project reports its frameworks, what an unrecognised MSBuild boolean means — is one
cheap, isolated case. Forty-four of them run in under a second and load no engine at all. This is
where the bugs are, so this is where the coverage is.

**The wiring is tested against real evaluation, and sparingly.** A handful of fixture projects are
evaluated by the actual MSBuild: that a real project evaluates, that the adapter copies the right
fields out of it, that a malformed project comes back as `APS2002` rather than an exception, and
that a snapshot still reads after the `ProjectCollection` that produced it is gone.

Three rules keep the second layer honest:

- **Fixtures declare package references and never restore them.** Evaluation reads what a project
  says, not what NuGet resolved, so nothing touches the network or the package cache. Resolved
  restore assets are Milestone 3 and will need their own answer to this question.
- **Fixtures carry an empty `Directory.Build.props`, `Directory.Build.targets` and
  `Directory.Packages.props`.** MSBuild walks upwards looking for those, and without them it would
  find this repository's — so every fixture evaluation would inherit whatever the repository happens
  to configure, and the tests would be asserting about the repository rather than about the fixture.
- **Paths come from `AppContext.BaseDirectory`.** The fixtures are copied beside the test assembly,
  so no test knows where the repository is.

## Consequences

- A mapping change is cheap to test, which is the point. A wiring change costs an evaluation, which
  is rare.
- The MSBuild test project is the first in this repository whose tests are not pure computation. It
  is measurably slower — about a second against the rest of the suite's milliseconds — and that is
  the honest price of proving the engine is actually driven.
- `MSBuildEnvironment` registration is process-global and irreversible, so the tests assert the
  property that survives repetition rather than trying to undo it: the first call decides and every
  later call agrees.
- A deliberately malformed fixture broke `AllProjectFiles` the first time the suite ran, because
  that helper assumed every `.csproj` in the repository parses. Fixtures are now excluded from it,
  which is correct on its own terms: they are data, not projects, and nothing builds them.
- If a future test genuinely needs restored assets, it needs a new decision rather than a quiet
  `dotnet restore` in a fixture. That would put the package cache back into the dependency set this
  ADR exists to keep out.

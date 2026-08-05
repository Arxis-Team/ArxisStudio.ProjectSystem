# 18. The adapter references Markup by source, and is the one package allowed to

Date: 2026-08-06
Status: Accepted

## Context

Milestone 7 adds `ArxisStudio.ProjectSystem.Markup.Xaml`, the adapter that turns a project snapshot
into the `XamlLoadEnvironment` that `ArxisStudio.Markup` loads a document in. It is the package the
whole family exists to make possible, and it is also the first one that breaks a rule the others
have kept absolutely.

Two questions had to be answered before a line of it could be written.

**How does it reach Markup?** Markup is a separate repository, sitting beside this one. It packs to
its own `artifacts/` directory at `0.2.0-preview.2`, and it is not on nuget.org. So the choices were
a relative `ProjectReference` across the two repositories, or a local package source pointing at
those artifacts.

**What happens to the dependency rules?** `ForbiddenDependencies` has forbidden `ArxisStudio.Markup`
and `Avalonia` in every package since the first commit. The adapter must reference both, and must
also *expose* Markup's types, because handing back a `XamlLoadEnvironment` is the entire point.

## Decision

**A relative `ProjectReference` to `../../../ArxisStudio.Markup/src/ArxisStudio.Markup.Xaml.Loader`.**

The two repositories are developed together. Referencing by source means a breaking change in Markup
fails here immediately, rather than at whenever somebody next remembers to pack it — and the
alternative builds against whatever was last packed, which today is already stale. A local feed
would also add a package source and a version to bump on every change, which is ceremony in exchange
for a boundary that is not real: nobody is consuming these from a feed
([ADR 0011](0011-these-libraries-are-referenced-not-published.md)).

The cost is that the repositories must sit beside each other. A `Target` in the adapter's project
file checks for Markup before the build reaches its references and fails with a sentence saying so,
rather than with an unresolved-path error naming a directory that does not exist.

**The `Hosts` table gains a row for the adapter: `ArxisStudio.Markup` and `Avalonia`.**

This is not a hole in the rule; it is the rule working. Every other package is kept away from Markup
and Avalonia *precisely so that one package can join them deliberately*, in the open, where the
dependency is the point rather than an accident. The same table already lets the MSBuild provider
host MSBuild and the package manager host NuGet, and nothing hosts two.

**`IsForbiddenInPublicApi` becomes per-package, and the adapter may expose Markup.** For the other
packages the rule is unchanged and stricter than the reference rule: the MSBuild provider references
MSBuild and still may not hand out a `ProjectInstance`. The adapter is the exception because a
consumer asking it for a load environment is already holding Markup. It is still kept away from
MSBuild and NuGet — adapting a model to Markup needs neither.

## Consequences

- **The dependency runs one way and nothing runs back.** A test asserts that no core package
  declares or compiles a reference to the adapter. A core that could see it would make Markup a
  transitive dependency of everybody reading a snapshot, which is the coupling the separate package
  exists to avoid.
- **CI must check out both repositories**, side by side. That is a real operational cost and the
  price of co-development.
- **Markup's projects must not be added to this solution.** `dotnet sln add` pulled all three in
  when the adapter was added, and they were taken back out: they belong to their own repository,
  build under their own settings, and a second solution owning them is a second place to keep in
  step.
- **The declarative dependency check earns its place here.** A mutation test proved it: adding a
  `ProjectReference` on the MSBuild provider to the adapter did not fail the compiled check at all,
  because a C# assembly records only the references it actually used. A project can declare an
  engine, carry it into every consumer's output, and leave no trace in its own metadata until
  somebody writes the first line that touches it. Reading the project file is what notices on the
  day the reference is added rather than the day it is used.
- If Markup is ever published properly, this is the decision to revisit, and the change is one line
  in a project file.

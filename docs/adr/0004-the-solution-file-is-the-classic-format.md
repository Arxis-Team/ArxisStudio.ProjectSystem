# 4. The solution file is the classic `.sln` format

Date: 2026-08-05
Status: Accepted

Numbered 0004 because 0001–0003 are reserved for the three ADRs the task specification requires by
name; they are written when the code they describe exists.

## Context

The task specification's repository layout asks for `ArxisStudio.ProjectSystem.slnx`, and allows
the classic format only under a technical condition: "If the pinned SDK cannot create or build
`.slnx`, use `ArxisStudio.ProjectSystem.sln` and explain the reason in the final report."

That condition does not hold here, and it is worth being exact about it rather than quietly
invoking the escape clause. The facts as measured:

- The installed SDK is 10.0.301. `dotnet new sln` offers `-f sln|slnx` and **defaults to `slnx`**.
- The repository was in fact built, tested and packed from a working `.slnx` first. It produced a
  clean build, 23 passing tests, and a valid package.
- Rider 2026.1 is the IDE in use and supports `.slnx` natively.

So `.slnx` was available, working, and preferred by the contract. The repository owner chose the
classic format anyway.

## Decision

Use `ArxisStudio.ProjectSystem.sln`.

This is a recorded deviation from the specification, and the reason is **consistency with the
sibling repositories**, not a tooling limitation. `ArxisStudio.Markup` and
`ArxisStudio.DesignEditor` both carry a classic `.sln`, and a family of three libraries that
disagrees about how its solutions are stored makes every cross-repository habit — scripts, IDE
muscle memory, documentation — hold in two places out of three.

The deviation is recorded here rather than only in a final report because a future reader will
find `.sln` next to a specification asking for `.slnx` and reasonably assume something failed.
Nothing failed.

## Consequences

- The solution file is roughly 56 lines where the equivalent `.slnx` is 8. Each project costs a
  GUID, an entry in `ProjectConfigurationPlatforms` for every configuration and platform, and a
  line in `NestedProjects`. Adding a project is a three-place edit rather than a one-line one.
- Merge conflicts in the solution file become likelier, because the configuration matrix is the
  part that conflicts and `.slnx` does not have one. With two projects this is theoretical; it
  stops being theoretical as the package family grows.
- `.sln` is the one file exempt from the repository's `eol=lf` rule. Visual Studio and Rider write
  it with a BOM and CRLF and rewrite it that way whenever they touch the solution, so normalising
  it would produce a spurious diff every time the solution is opened. `.gitattributes` pins
  `*.sln text eol=crlf` and `.editorconfig` matches.
- Nothing in the build, test, pack or CI path depends on the format: every command is a plain
  `dotnet` invocation and reads either. Revisiting this decision is a `dotnet solution migrate`
  away, and that command exists in this SDK.
- If the family later standardises on `.slnx`, all three repositories should move together. That
  is the same reasoning that produced this decision, applied in the other direction.

# 0027. A design set is one generation, loaded at once

Date: 2026-10-03

Status: Accepted. Amends [0021](0021-a-run-holds-one-generation-of-a-projects-types.md) and
[0023](0023-a-generation-is-reclaimed-before-its-successor-is-born.md).

## Context

[ADR 0021](0021-a-run-holds-one-generation-of-a-projects-types.md) says a run holds one generation of
a *project's* types, and the designer took it literally: a generation per project that owns an open
form. A designer shows forms of several projects — an application and the control library it
references, a second application on the same library — and a generation per owner loaded the
library once per owner: two copies of one assembly in the process, the failure 0021 exists to
prevent, reached by opening two forms.

Three more things a generation per owner got wrong:

- **It loaded lazily.** An assembly was read when a document first named it, so a build that ran
  between the generation's creation and that moment put two builds into one generation — the
  library from before it, the application from after it.
- **It hid shadowing.** The host's own copy of a name wins, which is what keeps one `Button` type in
  the process. A project whose assembly is named like one the host loaded was silently not shown.
- **It searched everything for every form.** A form of a library saw the types of the application
  that references it, which its build does not.

## Decision

**One generation for a design set** — every project whose documents a designer shows, and what they
reference — created by `ProjectAssemblyContext.Create(snapshot, projects)`:

- The union of what each project needs at run time, each file once. A library two projects
  reference is one assembly, and both their forms see one `Type`.
- Two different files of one name cannot both be loaded: the first in the set's order is, and
  `APS5001 AssemblyNameConflict` says which. A project's build of a name the host process already
  has is not loaded either, and `APS5002 ShadowedByHost` says so; only a separate process could show
  it.
- **Every assembly the projects build is loaded before `Create` returns.** The generation is one
  build of everything, so a host creates it when no build is running.

**An environment per project of the set**, from `ProjectXamlEnvironment.Create(generation, project, …)`:
types are searched in the project's own closure (`AssembliesOf`), as its build sees them; resources
are asked of a map the host keeps current (`Func<ProjectResourceMap>`), so a style sheet added in the
IDE is found without a new environment; and every environment of a generation shares one member
resolver, which holds the generation's types and goes with them. `CreateOptions` names the project's
own output as the load's `LocalAssembly` and loads forgivingly — bindings compile by default only
when asked, because a path that does not resolve yet is somebody still typing in the other editor.

The invariant of 0021 stands — at most one live copy of a type — and so does 0023's order: a
generation is reclaimed and proven gone before its successor is born. What changed is what one
generation is of.

## Consequences

- Opening forms of two projects that share a library loads the library once. Replacing the
  generation replaces it for every form at once, which is the only way it can be replaced.
- A new project with documents, or a change to what the set references, is a new set and therefore a
  new generation: the host treats a changed set as a stale generation, as it treats a rebuilt one.
- Loading everything at creation costs a read of every output up front, which the lazy load spread
  over the first opens. A designer opens forms of the projects it loads, so the reads were coming.
- A name conflict or a shadowed project is a diagnostic, not an exception, and the designer shows it
  rather than failing the set: everything else in it still loads.

## Amendment, 2026-10-04: a closure includes the packages that map a XAML namespace

A closure was what the projects build — their outputs and their project references — and a package
was reached only by naming its assembly. But a document never names a theme's assembly: `FluentTheme`
is written in `https://github.com/avaloniaui` and lives in `Avalonia.Themes.Fluent`, a package; so do
the data grid and the colour picker, and every third-party library of controls maps a namespace of its
own. Markup's type resolver searches the closure and whatever the process has loaded, so a host that
happened to load the package resolved the name, and the designer sample — a host built on Fluent —
always did. ArxisStudio, a host with a theme of its own, answered that the namespace mapped nothing it
could find: every form lost its application's theme, and any form with a data grid could not load.

So **a project's closure includes the packages of its runtime assets that map a XAML namespace** —
declare Avalonia's `XmlnsDefinitionAttribute` — after what the projects build, so a project's own type
still wins a name. The single-project environment searches them too. Whether a package maps one is
read from its metadata, without loading it: a package's assembly goes to the default context and stays,
and asking by loading would keep every restored package in the process for the question alone. A
package the host already has answers with the host's copy, as before.

The adapter's tests hold it with a package fixture of their own — a library that maps a namespace,
never referenced as an assembly — through `AssembliesOf`, the single-project search, and a type resolved
by the namespace either way.

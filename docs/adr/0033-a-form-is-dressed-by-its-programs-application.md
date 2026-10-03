# 0033. A form is dressed by its program's application

Date: 2026-10-03

Status: Accepted. Extends [0028](0028-the-design-host-replaces-a-generation-in-order.md); uses ArxisStudio.Markup's
ADR 0027.

## Context

A form does not say what it looks like; its program's application does. `App.axaml` sets the theme in
`Application.Styles` — Fluent, Simple, a library's — and declares in `Application.Resources` what the
form's `{StaticResource}` and `{DynamicResource}` find. A designer shows the form inside the designer's
own application, and that is the one Avalonia dresses it with.

`UiDesigner.Demo` got away with it: its own application carries Fluent, and most forms are written for
Fluent. ArxisStudio has no general-purpose theme at all — its own dresses the studio's controls and the
roots of its trees — so a form's `Button` there has no template and shows nothing, and the resources the
form names are the studio's or nobody's.

Loading `App.axaml` the ordinary way constructs its class: the program's `App`, whose constructor is the
program starting up inside the designer.

## Decision

**The host loads what a form's application declares, for that form.** `OpenApplicationAsync(document)`
finds the application — the document's own project's markup whose root resolves to an application, or,
for a library with none, that of the first project of the design set that references it, in snapshot
order — and loads it in the live generation as written (Markup's `XamlClassUse.AsWritten`): a plain
`Application` holding the document's styles, resources and data templates, its class never constructed.
`App.axaml` is read first among a project's markup, the convention every template follows; which
document is a project's application is remembered until a snapshot is published or markup is saved.

**One per form.** Avalonia gives a style and a resource dictionary one owner, so a form takes its
application's over — moves them into its own scope — and each call loads the document again. Forms are
few and the document is small; a shared application would need a style to belong to two forms.

**It belongs to a generation, and lets go of it.** What an application declares may be of the
project's types. A swap closes every application still open after the participants let go and before
the generation is asked to go, and a closed `ProjectDesignApplication` drops its session — the test that
held one across a swap found that holding the object held the generation through it. A saved application
document is `ApplicationChanged`, and a designer answers it, as it answers a swap, by opening again.

## Consequences

- A designer whose own application dresses nothing shows forms as their program does. ArxisStudio's form
  item lays a form's application on the area that stands in for the program's application.
- What the runtime compiler built while loading the document belongs to the generation, styles included.
  A form that took them over gives them back or drops them when the host asks the designer to let go; a
  test that showed them in a window and closed it held its generation past the test, and every
  generation after it in the run found its predecessor held.
- A form whose project has no application, and no project of the design set references one that does, has
  nothing to be dressed by; the designer says so rather than dressing it with its own theme.
- A resource the form names that its application lacks is still searched for up the designer's tree, as
  Avalonia searches it, and may be found in the designer's own application. That is a difference from the
  program the host cannot remove inside one process; a designer can point it out.
- A renamed or deleted application document is noticed through the next snapshot, not through
  `ApplicationChanged`; a designer that wants the new one opens again after the snapshot moved.

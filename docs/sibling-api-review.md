# The siblings' public API, reviewed by building a designer against it

Date: 2026-08-07
Reviewer: built `samples/FormsDesigner` against `ArxisStudio.DesignEditor` and `ArxisStudio.Markup`
Status: findings; the one recommendation that belongs to this repository is done

## The question

FormsDesigner exists to answer one: *can the public API of the three libraries be used to build a
whole program designer — project tree, form canvas, toolbox, property inspector, run and package
management — without reaching past what those libraries publish?*

It is a better question than a checklist because it cannot be answered optimistically. Either the
sample compiles against public types and does the job, or somewhere it has to invent, work around,
or reach into something it should not have to.

## Verdict

**Yes, with one exception, and the exception is important.**

FormsDesigner is roughly three thousand lines. It references the three libraries and Avalonia and
nothing else. It loads a real solution, evaluates it through MSBuild, opens `.axaml` documents,
renders them live, edits them by direct manipulation, writes the edits back as XAML text, restores
and builds and runs the project, and searches and installs NuGet packages. Every one of those goes
through published API.

The exception is that **a form whose root is a `Window` is not something either library has a story
for**, and that is the most common form in any Avalonia application. Everything else below is either
smaller than that or turned out on inspection not to be a gap at all.

## What the API does well

### `ArxisStudio.Markup` — the strongest of the three

The loader's shape is the thing that makes a designer possible rather than merely conceivable.
`XamlLoadSession` holds a document, the live objects it produced, and — through `XamlObjectMap` —
the correspondence between them in both directions. That correspondence is the hinge of the whole
application. A designer's central act is "the user clicked this rectangle on screen; change the text
that produced it", and without a map that answers `GetElement(runtimeObject)` there is no honest way
to do it. Walking the two trees in parallel is the obvious alternative and it is wrong, silently,
the first time a template expands.

`XamlObjectOrigin` deserves particular note. A click can land on something a template generated, and
the map says so instead of handing back the nearest declaration as though editing it were the same
thing. That distinction is what stops a designer corrupting documents in ways the user cannot see.

`XamlDocumentEditor` is a complete structural vocabulary — insert, remove, move, replace, wrap,
unwrap, duplicate — returning text changes, so edits land in the user's file as edits rather than as
a reformat of the whole document. Combined with `XamlWorkspace`'s undo and redo, FormsDesigner got
history for free.

`XamlLoadMode.Design` applying `Design.DataContext` and `d:` attributes is what makes a loaded form
look like a form instead of an empty box.

The documentation is unusually good: an 1858-line README, six API guides under `docs/api`, ADRs, and
a `limitations.md` that argues its omissions rather than listing them.

### `ArxisStudio.DesignEditor`

The editor's central decision — it reads the tree and never writes it, reporting `DeleteRequested`
and `ReorderRequested` as requests the host fulfils — is what let FormsDesigner keep the document as
the single source of truth. An editor that mutated the visual tree would have left the document and
the screen able to disagree, and every form designer that has ever done that has shipped the bug.

`DesignEditorInputGestures` and `DesignEditorInteractionOptions` as `AvaloniaObject`s settable from
XAML meant the entire gesture model — pan button, marquee modifiers, snap tolerance, nudge steps,
container-entry modifier — is configuration rather than code. `DesignSelectionTarget` carrying
`Container`, `Target`, `Scope` and `Depth` is exactly what a hierarchy panel and a property
inspector both need.

`ContentMode="Loaded"` is the mode a designer wants and it exists, including the part that is easy
to forget: input is swallowed so the form does not live its own life under the pointer, and hit
testing is done in world coordinates so selection still works.

### `ArxisStudio.ProjectSystem` (this repository)

Nothing in the sample needed a type this repository does not publish. `PackageReferenceInfo.Origin`
was added during the work — a designer must tell a reference the project declares from one an import
supplied, and it could not — which is the one place the core was short and is now not.

## The gap that matters: a `Window` cannot be a designable surface

Every Avalonia application's main form is a `Window`. In Avalonia a `Window` is a `TopLevel` and is
parented to a `TopLevelHost` at construction; making it the content of anything throws during
layout. The exception, proved with a throwaway probe, is
`InvalidOperationException: … already has a visual parent TopLevelHost`.

Neither library has anything for this.

- The editor's README describes the loaded-form case and ends "its markup root is an ordinary
  Avalonia panel". Its worked example is `AvaloniaRuntimeXamlLoader.Load` of a file, which throws for
  every `MainWindow.axaml` in existence. The word `Window` does not appear in the README.
- The loader has `IXamlRootInstanceFactory`, which lets a host decide *how* the root instance is
  made — but not *what* it is. The type comes from `x:Class`, and the loader validates it
  (`InvalidRootFactoryResult`), so a surrogate cannot be substituted.

FormsDesigner therefore invents the answer, in `FormViewModel`: it keeps `Root` — the object the
document produced, which is what edits address — separate from `Surface`, which is what the canvas
hosts. On publication it detaches `Window.Content`, hosts that, and draws the window's chrome itself.
The detach has a consequence that took a while to find: pulling the content out of the window pulls
it out of the window's `DataContext` too, and `Design.DataContext` is set on the root, so every
binding underneath goes blank and the form measures to nothing — which looks exactly like a form that
failed to load. The context has to be carried across by hand.

That is a competent workaround, and it is the wrong place for it. Every consumer that builds a
designer will hit this, will hit the `DataContext` consequence second, and will solve it differently.

**Recommendation.** One of the two libraries should own it. The cleaner of the two options is the
loader: a load mode, or an option on `XamlLoadOptions`, that loads a `Window`-rooted document as a
designable surface — the root's properties still addressable for editing, the content returned
already detached, the design-time context already carried. The alternative — a `DesignEditorItem`
that recognises a `TopLevel` and hosts its content — puts Avalonia-window knowledge in the editor,
which is a worse fit, but is still better than each consumer discovering it.

## Smaller findings

**The editor has no `docs/`.** Markup has ADRs, six API guides and an argued `limitations.md`; the
editor has a README and a demo. The README is good and thorough, but it is one file that mixes
tutorial, reference and changelog, and there is nowhere recording *why* the editor never writes the
tree — which is its single most important decision and the one a contributor is most likely to undo.

**The editor's README is in Russian and its XML docs are in English.** Not a defect, but a consumer
reading the surface in an IDE and the guide on disk gets two registers.

**`DesignEditorItem` does not paint its card.** Its `ControlTheme` is transparent throughout, so a
form whose document sets no `Background` shows whatever is behind it. That is a defensible choice —
the item is a frame, not a surface — but it is undocumented, and it is the sort of thing a consumer
will mistake for a bug in their own code. One sentence in the README would cost nothing.

## Three things that looked like gaps and were not

Recording these matters as much as the findings, because two of the three were my own errors and one
of them was expensive.

**The object map had no entry for an `x:Class` root.** Real, and now fixed in Markup. The root of an
`x:Class` document is created before the markup loads and handed over already made, so Avalonia
records no build position for it — the one element whose object is known without evidence was the
one element with no entry, and a five-element document mapped four. The fix asserts the pair the
arguments already mean, and only where the walk left both sides free. Consequence for a designer:
clicking a form's background found nothing and the form's own properties were unreachable.

**`x:Class` types would not resolve.** Not Markup's gap. `XamlTypeResolver` already takes the
assemblies to search; this repository's adapter was not passing the project's own output. Fixed here,
in `ProjectXamlEnvironment`.

**A form with no `Background` showed a light card.** Not a gap, and not real. Every reading that
said the token was not reaching the card came from a run started with `--no-build` against a binary a
failed build had not replaced. With the build confirmed, `{DynamicResource Bg1}` renders dark. The
palette of duplicated hex values written to work around it has been deleted, along with the three
view-model properties and the variant plumbing that fed it.

That last one is the cautionary tale in this document. A measurement taken against a binary you did
not watch get built is not a measurement — it is the previous answer, repeated back convincingly. It
produced a fabricated limitation, an unnecessary workaround, and a paragraph of documentation
explaining a defect that did not exist.

## Summary

| | |
|---|---|
| Can a whole designer be built on the published API? | Yes |
| Did the sample reach past public API anywhere? | No |
| Did it have to invent anything substantial? | One thing: hosting a `Window`-rooted form |
| Gaps found in `ArxisStudio.Markup` | One, fixed during the work (the object map's root) |
| Gaps found in `ArxisStudio.DesignEditor` | The `Window` story; no `docs/`; card painting undocumented |
| Gaps found in this repository | One, fixed: `PackageReferenceInfo.Origin` |

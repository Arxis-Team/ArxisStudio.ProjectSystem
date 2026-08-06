# FormsDesigner — the visual designer sample

A form designer built on all three families at once: an infinite canvas holding live Avalonia
controls, a property inspector, a toolbox you drag from, and the project machinery to restore, build
and run what you are designing.

```bash
dotnet run --project samples/FormsDesigner
```

Pass a project to skip the dialog, and a form name to open one straight away — the output pane is
mirrored to standard output, so this doubles as a smoke test:

```bash
dotnet run --project samples/FormsDesigner -- C:\src\App\App.csproj MainView.axaml
```

It needs **both** `ArxisStudio.Markup` and `ArxisStudio.DesignEditor` checked out beside this
repository. The project file fails with `APSSAMPLE01` and a sentence saying so if the second is
missing.

## Who does what

The division is the whole point, and none of the three knows about the others.

| | Responsibility |
| --- | --- |
| `ArxisStudio.ProjectSystem` | which project is open, which files it contains, what it resolves to, restore, build, run, NuGet |
| `ArxisStudio.Markup` | the document: parsing it, **every** edit to it, and building the live objects |
| `ArxisStudio.DesignEditor` | the surface: viewport, grid, selection, handles, gestures |

Every gesture takes the same route. The editor reports it, the view model turns it into a document
edit, and the live tree is rebuilt from the document. **The document is the truth and the canvas is a
view of it** — a canvas that could disagree with the file is a designer that loses work.

## What each part is a view of

| Panel | The API behind it |
| --- | --- |
| Project | `ProjectSnapshot.Items` filtered to markup — a file the project does not compile is correctly absent |
| Toolbox | markup snippets, dragged with Avalonia's own drag-and-drop |
| Canvas | one `DesignEditor` item per open form, whose content is `XamlLoadSession.RootObject` |
| Inspector | the selected element's **attributes**, not the live control's hundred properties |
| Packages | `NuGetHttpFeed`, `GetMetadataAsync` for the advisory line, `PackageInstaller.ApplyAndRestoreAsync` |
| ▶ / ■ | `OutputArtifactKind.Assembly` and `RuntimeConfiguration`, built through `ExecuteAsync` first |
| Console | everything above, said out loud |

## Five things it demonstrates on purpose

**A click on screen finds the line that drew it.** `XamlObjectMap` runs both ways. Templates produce
controls no element made — a button's own border, the text inside it — so the walk goes up through
the parents until it finds one that is mapped, which is how clicking a button's label selects the
button.

**The inspector shows what the file sets, not what the control has.** A `Button` has upwards of a
hundred properties and an author wrote three of them. Listing all hundred tells nobody which ones
matter.

**A move is written only where a position means something.** Inside a `Canvas` it becomes
`Canvas.Left` and `Canvas.Top`. Inside a `StackPanel` there is nothing to write — the panel owns the
position — so the designer says so and writes nothing, rather than faking it with a margin that
changes meaning the moment somebody adds a sibling.

**Deleting is a request, not an action.** The editor never edits the tree; it raises
`DeleteRequested` and does nothing until a subscriber marks it handled. That subscriber is here, and
what it does is edit the document.

**An edit is applied to the document first and to the live objects second.** `ApplyDocumentUpdateAsync`
works out what actually changed, so setting one property does not tear down the form around it.

## What it is not

**A form with an uncompiled `x:Class` cannot be shown.** The document names a type, and a type that
has not been built does not exist. Build the project and the form opens. The designer says exactly
which type was missing and lists what the load context held, because "did not load" on its own tells
nobody anything.

**The toolbox is Avalonia's controls, not the project's.** Reflecting over the project's assemblies
for placeable types is a real feature and a different one: it needs a rule for what counts as a
control somebody would place, and sensible initial markup per type.

**Properties are added by name.** The assembly context is right there and a typed editor per property
kind could be built on it. A name and a value is enough to show that the edit reaches the file.

**Undo is the file's.** Nothing here stacks edits; save writes the document and that is that.

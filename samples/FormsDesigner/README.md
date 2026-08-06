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

## The design

The window follows the ArxisStudio UI mockup: its palette, its type, its measurements, and its
arrangement — a 42px toolbar with Run centred, a 262px column of Hierarchy over Toolbox, the canvas
under a breadcrumb and a Design/XAML/Split switch, a 212px dock of Project, Console, Problems and
Packages, a 302px Inspector grouped into Layout, Appearance and Content, and a 26px status bar.

Both variants of the palette ship, keyed by `ThemeVariant`, and the toolbar switches between them —
including the canvas grid, whose colours are the same tokens under the keys `ArxisStudio.DesignEditor`
reads. Every colour in the window is a token; there is no literal outside `Views/Theme.axaml`.

The icons are the mockup's own paths, in `Glyphs`, together with the hue each kind of control is
drawn in — layout panels one colour, text entry another, lists a third. That is what lets a tree of
thirty rows be read by shape and colour before any of the names are.

### Checking it against the design

```bash
dotnet run --project samples/FormsDesigner -- <project> <form> --shot out.png
```

Opens, waits for the window to settle, writes exactly what is on screen, and exits. A designer is a
thing you look at, so "does it work" and "does it look right" are different questions and only the
first was ever answerable from a log.

### One thing that does not match yet

A form whose document sets no `Background` shows a light card on the dark canvas where the design
shows a dark one. It is not the card: painting the template's own panel red fills that rectangle, so
nothing above it is at fault, and resolving the token explicitly for the dark variant through
`Application.TryGetResource` does not change it either. Something under the item paints it, and I
have not identified what. Every form that sets its own background is unaffected.

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

## The gestures

Configured as `ArxisStudio.DesignEditor` documents them, mapped for a form designer.

| Gesture | What it does |
| --- | --- |
| drag | moves the control under the pointer |
| `Ctrl` + drag | moves or resizes the **form** instead — that is what `ContainerInteractionModifiers` is for |
| drag on empty space | marquee selection |
| `Shift` + click | adds to the selection |
| middle drag | pans; wheel zooms |
| arrows / `Shift` + arrows | nudge by 1px / 10px, and each press is one edit |
| `Alt` | bypasses snapping for the length of one gesture |
| `Delete`, `Esc`, `Ctrl+A` | remove, deselect, select every form |

Snapping is on — to the grid and to the neighbours' edges and centres — and both are checkboxes on
the toolbar. Resize is contained to the form, because a button hanging outside the window it belongs
to is a picture of something that cannot happen at runtime.

**Dragging within a flow layout only works because this application answers.** The editor reads the
control tree and never writes to it, so a reorder is a request: until something marks it handled, no
insertion point is drawn and nothing moves. The handler is here and what it does is edit the
document. It uses the request's `Anchor` rather than its index, because the editor counts a panel's
children and the document counts its content elements, and the two disagree the moment a parent
holds a property element — which a `Grid` with row definitions does.

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

**A form is built before it is opened, once, when it has to be.** A document naming an `x:Class`
names a type, and a type in a project nobody has compiled does not exist — so opening a form in a
freshly created application used to fail with "unable to resolve type", which is true and
unactionable. The designer builds instead, and says so. A document with no `x:Class` needs nothing
built and waits for nothing.

**A document is parsed with the `avares` URI it will be embedded under.** Without one a relative URI
means nothing, and a new Avalonia window says `Icon="/Assets/avalonia-logo.ico"` on its second line.

**The editor's theme has to be merged, or there is no editor.** `ArxisStudio.DesignEditor` ships its
template, its grid, its selection adorners and its resources in one dictionary:

```xml
<ResourceInclude Source="avares://ArxisStudio.DesignEditor/Themes/ArxisStudioDesignEditorTheme.axaml" />
```

Without it the control has no template and draws nothing — no grid, no forms, no handles — while
every log line still says the document loaded, because it did.

**A window is drawn, not hosted.** A `Window` cannot be a child of anything — Avalonia gives one a
`TopLevelHost` parent the moment it is constructed, and putting it in a `ContentControl` throws
*during layout*, off the stack of everything that could report it, so the canvas simply stayed empty.
The designer takes the content out of the window, shows that, and paints the title bar itself — and
carries the window's data context across with it, because `Design.DataContext` is a property of the
root and content taken out of the root loses it.

**Documents load in `XamlLoadMode.Design`.** A form is usually a page of bindings with nothing bound
at rest; the Avalonia template's window is one `TextBlock` reading `{Binding Greeting}`. Design mode
is what applies `Design.DataContext` and the `d:` attributes the document supplies for exactly this.

**The toolbox is Avalonia's controls, not the project's.** Reflecting over the project's assemblies
for placeable types is a real feature and a different one: it needs a rule for what counts as a
control somebody would place, and sensible initial markup per type.

**Properties are added by name.** The assembly context is right there and a typed editor per property
kind could be built on it. A name and a value is enough to show that the edit reaches the file.

**Undo is the file's.** Nothing here stacks edits; save writes the document and that is that.

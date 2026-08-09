# FormsDesigner — the visual designer sample

A studio built on all three families at once: a welcome screen that creates and opens projects, an
infinite canvas holding live Avalonia controls, a property inspector, a toolbox you drag from, and
the project machinery to restore, build and run what you are designing.

```bash
dotnet run --project samples/FormsDesigner
```

That opens the welcome screen: the projects you had open, four templates to start from, Open, and
Clone from Git. Pass a project to skip it, and a form name to open one straight away — the output
pane is mirrored to standard output, so this doubles as a smoke test:

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

### The two that used to not match

Both are closed, and both were closed by measuring rather than by reasoning — which is worth
recording, because in each case the reasoning had been confidently wrong.

**The window no longer keeps the system title bar.** Avalonia 12 removed
`ExtendClientAreaChromeHints`; the replacement is `Window.WindowDecorations`, and the value that
fits is `BorderOnly` — the frame stays, so resizing, snapping and the shadow are still the
platform's, and only the caption goes. The toolbar is then the title bar, which costs four handlers
in the code-behind: minimise, maximise, close, and a press that calls `BeginMoveDrag`. That last one
has a trap in it. Testing `e.Source is Border { Name: "TitleBar" }` never matches, because the
toolbar's own `Grid` covers it completely; the question to ask is not "is this the bar" but "was
anything clickable under the pointer", so the handler walks up from the source and stops at the
first `Button`, `TextBox`, `ComboBox` or `MenuItem`.

**A form that sets no `Background` now shows the dark card the design shows.** This one was never
a limitation at all. The card was always the right layer — painting it magenta filled exactly the
rectangle in question — and the token was always reaching it. Every reading that said otherwise came
from a run started with `--no-build` against a binary the failed build had not replaced. With the
build confirmed, `{DynamicResource Bg1}` on the card panel renders dark, and the palette of hard
values written to work around it has been deleted.

Two lessons, and the second is the sharper one. A rendering question is answerable in about a
minute with `--shot` and unanswerable for an hour without it. And a measurement taken against a
binary you did not watch get built is not a measurement — it is the previous answer, repeated back
convincingly. The second finding above was invented entirely by that mistake.

### The header

The design's header is a 42px row, and it is reproduced control for control: the 24px mark, the
project, the branch, the configuration, then search, the run bezel, settings, a rule, and the
window's three buttons.

Two of those read state nothing else in the window has. The **branch** is asked of `git` rather
than parsed out of `.git`, because a head can be a symbolic ref, a detached hash, or a worktree
pointing elsewhere, and `git` already knows the difference; a project outside a repository shows no
branch at all rather than a plausible-looking `main`. The **run bezel** is two different sets of
controls — run, rebuild and a target while idle; a counting badge, restart and stop while
something is running — and the target it names is a runnable project, decided by the same test the
run path applies, which is that the project produced a runtime configuration.

Two deliberate departures, both so that nothing is lost rather than for their own sake. The
mockup's settings icon is decorative; here it opens the menu holding the theme, the two snap
toggles and restore, which is where the controls the header no longer shows individually went. And
the mockup's running bezel has a pause button, which is omitted: a `dotnet` process cannot be
paused, and a button that cannot do its job is worse than no button.

The keyboard reaches what the toolbar no longer shows: `Ctrl+O`, `Ctrl+S`, `Ctrl+N`, `Ctrl+B`, `F5`.

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
| Project | folders on the left, the folder's files on the right; **double-click a file to open it**. The tree is every item the project compiles, so a `.cs` file is there and says why it does not open; the forms are `ProjectSnapshot.Items` filtered to markup, and a file the project does not compile is correctly absent |
| Toolbox | markup snippets, dragged with Avalonia's own drag-and-drop. A drop lands in the deepest control the document owns whose rectangle contains the pointer and which can hold a child, so a control goes into the panel it was dropped on rather than at the top of the form. By rectangle rather than by hit-testing, for the same reason the editor decides selection that way: a loaded form takes no input, and a panel that paints no background renders nothing to hit |
| Hierarchy | both directions. The canvas reports a selection through `DesignSelectionChanged` and the object map turns the control into an element; the tree returns the favour through `DesignEditor.SelectDesignTarget`, which the map answers the other way round. That method did not exist until this sample needed it |
| Canvas | one `DesignEditor` item per open form in `ContentMode="Annotated"`, and the item's content is the form and nothing else. In `ContentMode="Loaded"` the editor offers every control the author wrote as a design target and finds them by walking the container's content, so a caption or a title bar put in there is — correctly, and unhelpfully — something the user can select and resize. The card is the container's own `Background`, `BorderBrush` and `CornerRadius`; everything else the designer draws sits in a layer above the canvas, in world coordinates, through the editor's public `ViewportTransform`. What is editable is stated rather than guessed: after every load the object map says which controls have a document element behind them, and those are marked `Layout.IsTracked` |
| Inspector | the properties a control actually has, offered whether or not the document has written them — a short curated list per group, each name asked of the control first, so a Border is offered `CornerRadius` and a TextBlock is not, plus whatever the parent attaches (`Canvas.Left` inside a Canvas) and anything else the file says. Clearing a field removes the attribute. One editor per kind of value, which the document cannot decide and `XamlMemberDescriptor` can: a `bool` is a checkbox, an enum is its own values (side by side when there are four or fewer, a drop-down when more), a brush shows its colour, a number is a number. `ConvertFromText` is asked before anything is written, so text the load could not have meant is reported instead of saved. A value that is a binding is shown and not edited — typing a literal over one would replace it with whatever the text looked like |
| Resize | `EditCompleted` on release, written to the element the control came from — and for the card itself, to the document's root, because a gesture on the card is a gesture on the form. A control with nothing behind it says so instead of skipping the write in silence |
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
| `Ctrl+Z`, `Ctrl+Y` | back through the documents, and forward |
| `Ctrl+C`, `Ctrl+X`, `Ctrl+V`, `Ctrl+D` | copy, cut, paste, duplicate — copies carry no names |
| `Ctrl+S`, `Ctrl+Shift+S` | save this form, save every form that has edits |
| `Ctrl+0` | fit the form to the window |

Snapping is on — to the grid and to the neighbours' edges and centres — and both are checkboxes on
the toolbar. Resize is contained to the form, because a button hanging outside the window it belongs
to is a picture of something that cannot happen at runtime.

**Dragging within a flow layout only works because this application answers.** The editor reads the
control tree and never writes to it, so a reorder is a request: until something marks it handled, no
insertion point is drawn and nothing moves. The handler is here and what it does is edit the
document. It uses the request's `Anchor` rather than its index, because the editor counts a panel's
children and the document counts its content elements, and the two disagree the moment a parent
holds a property element — which a `Grid` with row definitions does.

## The XAML pane

`AvaloniaEdit`, read-only, with line numbers and the editor's own XML rules. It used to be a text
block with nowhere to scroll sideways, so a document whose attributes ran past the pane's width was
simply cut off — and every one of them does.

Only the colours are replaced, and they come from the same tokens as the rest of the window, so the
pane follows the light and dark palettes with everything else. Two things about that are worth
knowing, because both cost an attempt: a window that is not attached yet finds none of the
application's resources, so the painting happens on `Opened` rather than in the constructor; and the
palette lives in theme dictionaries, so a lookup that does not say which variant it wants finds
nothing in one.

It stays read-only on purpose. The document is edited through the canvas and the inspector, and a
pane that also accepted typing would be a second editor of the same file with no answer for what
happens when both change it.

## Making an application in it

New Project writes a real Avalonia application — `csproj`, `Program`, `App`, a window and a view
model — rather than shelling out to `dotnet new`, whose Avalonia templates are a workload a machine
need not have. Four shapes: an empty application, a chat, a dashboard and a media player. The
generated project turns central package management off for itself, or being created inside a
repository that manages versions centrally would fail to restore for a reason no dialog mentioned.

From there it is the designer: drag controls from the toolbox onto the form, arrange them, edit
their properties in the inspector, `Ctrl+Z` and `Ctrl+Y`, copy, cut, paste and duplicate, save, and
press Run.

Two things had to be true for that to work at all, and both are about a window-rooted form — which
is what every template's main form is:

**A project that has never been restored cannot be built**, and the designer builds before opening a
form whose `x:Class` names a type. So the restore comes first, and only when the build was going to
happen anyway.

**A window is whole while it is being updated.** The stand-in shows a window by taking its content
out of it; an update reads the live tree to work out what to change, and a live window with no
content had nothing to change — so the document gained a control, the canvas did not, and the next
drop had no container to land into. The content goes back for the length of the update and is
borrowed again afterwards.

### Beside another IDE

The designer is meant to sit next to Rider or Visual Studio: the layout is done here, the code is
written there, and the same files are open in both. Three things follow from that, and all three
were reported as bugs before they were features.

**It builds into `bin/ArxisStudio`.** Two tools cannot own `bin/Debug`: the moment one of them has
the application running, the other's build stops with a dozen lines of MSB3026 and then MSB3027 —
"the file is locked by .NET Host" — which is true, unactionable, and looks like the designer is
broken. The property is passed to the evaluation as well as to the build, because the run path
starts what the evaluation says the project produces. The intermediate folder stays shared, so the
restore is not done twice.

**It follows the files.** A change to an open `.axaml` is applied to the form as a document update,
so the session, the canvas and the tabs survive and the change joins the undo history — an edit made
in the other editor can be taken back here. A file appearing, going away or being renamed re-reads
the workspace whatever its extension is, because an SDK project takes its items from globs: a class
added in Rider is a new item, and the panel that lists them is stale until the evaluation is done
again. A file merely saved is not, because that changes no glob. And a form deleted while it is open
here closes its tab, rather than leaving one editing a document with nowhere to save to.

Events are coalesced over a quarter of a second, and the noise is dropped first: builds write under
`bin` and `obj`, tools keep state in dot-directories, and editors save through temporary files —
re-evaluating a project for any of those would be a second of nothing, repeatedly.

**It does not overwrite your unsaved work.** A form with edits that are not in the file is not
reloaded; it says so, and the next save is the person's decision.

### The check that says it works

```bash
dotnet run --project samples/FormsDesigner -- --verify <folder>
```

Creates a project through the welcome screen, opens its window, drops three controls from the
toolbox into it, edits one through the inspector's own rows, duplicates and pastes a control, undoes
and redoes every step, saves, restores, builds, runs the result and stops it. Every step goes
through the view model rather than around it — a check that wrote the markup itself would only prove
that the check can write XAML.

It ends with one line: `VERDICT ok — a project was created, laid out, built and run`. Both of the
window-rooted defects above were found by it.

## Five things it demonstrates on purpose

**A click on screen finds the line that drew it.** `XamlObjectMap` runs both ways. Templates produce
controls no element made — a button's own border, the text inside it — so the walk goes up through
the parents until it finds one that is mapped, which is how clicking a button's label selects the
button.

**The inspector offers a short list and asks the control about every name on it.** A `Button` has
upwards of a hundred properties and listing all of them tells nobody which ones matter — but an
inspector that shows only what the file already sets is a text editor with extra steps, because
nothing new can be added without typing the property name. So there is a curated list per group,
every name on it is offered only when the member resolver says this control has it, and anything the
document sets that is not on the list is appended.

**A move is written only where a position means something.** Inside a `Canvas` it becomes
`Canvas.Left` and `Canvas.Top`. Inside a `StackPanel` there is nothing to write — the panel owns the
position — so the designer says so and writes nothing, rather than faking it with a margin that
changes meaning the moment somebody adds a sibling.

**Deleting is a request, not an action.** The editor never edits the tree; it raises
`DeleteRequested` and does nothing until a subscriber marks it handled. That subscriber is here, and
what it does is edit the document.

**An edit is applied to the document first and to the live objects second.** `ApplyDocumentUpdateAsync`
works out what actually changed, so setting one property does not tear down the form around it.

**Undo is documents, not operations.** Every edit produces a whole new immutable document, so the
history is the documents themselves and going back is applying one the designer was already holding
— through the same path every other change takes. Nothing has to be right about inverting an insert.
And the selection is a `XamlElementPath` rather than an element or a control, because both of those
are replaced by the edit: it survives, and when what was selected has just been deleted the path's
parent is the answer.

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

This sample answered that itself for a while: take the content out of the window, show that, paint
the title bar, and carry the data context across by hand because `Design.DataContext` is a property
of the root and content taken out of the root loses it. That answer is
`ArxisStudio.Markup.Xaml.Design` now, where it belongs — every host that shows forms meets the same
wall and would meet the same consequences after it, in the same order. `FormViewModel` holds a
`XamlDesignSurface` and calls `Attach` after every publication; what is left here is the frame the
designer draws around it, and the title it reads off the surface.

**Documents load in `XamlLoadMode.Design`.** A form is usually a page of bindings with nothing bound
at rest; the Avalonia template's window is one `TextBlock` reading `{Binding Greeting}`. Design mode
is what applies `Design.DataContext` and the `d:` attributes the document supplies for exactly this.

**The toolbox is Avalonia's controls, not the project's.** Reflecting over the project's assemblies
for placeable types is a real feature and a different one: it needs a rule for what counts as a
control somebody would place, and sensible initial markup per type.

**Properties are added by name.** The assembly context is right there and a typed editor per property
kind could be built on it. A name and a value is enough to show that the edit reaches the file.

**Undo is the file's.** Nothing here stacks edits; save writes the document and that is that.

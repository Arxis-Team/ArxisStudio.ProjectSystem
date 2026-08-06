# ProjectSystem.Ide — the sample

An Avalonia window that uses every package in this family at once. It is not a good IDE and does not
try to be one; it is the shortest honest answer to "what does this library actually give me", and it
favours showing what the model knows over hiding it.

```bash
dotnet run --project samples/ProjectSystem.Ide
```

Pass a path to skip the dialog, which is also how it doubles as a smoke test — the output pane is
mirrored to standard output:

```bash
dotnet run --project samples/ProjectSystem.Ide -- C:\src\App\App.sln
```

Add `--run` to build and start the first project once it has loaded, which is what makes the Run
button checkable without a mouse:

```bash
dotnet run --project samples/ProjectSystem.Ide -- C:\src\App\App.csproj --run
```

It needs `ArxisStudio.Markup` checked out beside this repository, because the adapter it uses does
([ADR 0018](../../docs/adr/0018-the-adapter-references-markup-by-source.md)).

## What each panel is a view of

| Panel | The API behind it |
| --- | --- |
| Solution tab | `SolutionSnapshot.Projects`, `Folders`, `TryGetFolder`, and each project's `Items` grouped by item type |
| Files tab | the same `Items`, arranged by where they sit under `ProjectDirectory`, plus every reference list as `Dependencies` |
| Project | every field of `ProjectSnapshot` — identity, evaluated context, surfaced properties |
| References | project, framework, assembly and analyzer references, with aliases |
| Outputs | `Outputs`, `GetRuntimeAssemblies`, and `EvaluationInputs` |
| Packages | `NuGetHttpFeed`, `PackageVersions.Latest`, `GetMetadataAsync` for the advisory line, `PackageInstaller.ApplyAndRestoreAsync` |
| Resolved | declared `PackageReference` items beside what restore actually resolved |
| XAML | `ProjectXamlEnvironment`, `ProjectAssemblyContext`, `ProjectResourceMap`, and `ProjectMarkupDiagnostics` translating what the parser found into the same list as the project's own |
| Toolbar | `LoadAsync`, `RefreshAsync`, `ExecuteAsync` for restore, build, rebuild and clean |
| Run / Stop | `OutputArtifactKind.Assembly` and `RuntimeConfiguration`, built through `ExecuteAsync` first |
| Status | `WorkspaceVersion`, and `Invalidate` over a watched, coalesced batch of file changes |

## Three things it demonstrates on purpose

**Nothing refreshes on its own.** The watcher, the coalescer and `Invalidate` are composed here
rather than inside the workspace, which is what
[ADR 0016](../../docs/adr/0016-watching-belongs-with-the-provider.md) describes. The status bar says
what went stale; refreshing is a button, because a designer mid-edit is the wrong moment to reload.

**An operation is not a mutation.** Building publishes no snapshot and does not advance the version.
After a *restore* the sample refreshes explicitly, because a restore rewrites `project.assets.json`,
which is an evaluation input — so the model is genuinely behind the disk and nothing but a refresh
fixes that.

**The Files tab keeps only items whose file exists**, and that is not belt and braces. A project
evaluates to more than its contents: `PotentialEditorConfigFiles` alone contributes an
`.editorconfig` and a `.globalconfig` for every directory holding source, none of which exists —
MSBuild builds the list precisely so it can test each one. Without the check, two phantom files
appeared in every folder. The folder structure still comes entirely from the model; nothing
enumerates a directory, so a file no item includes is correctly absent. The asking is done off the
UI thread and once per distinct path, because a real solution evaluates to thousands of items and
that many file-system round trips between two frames is a window that has stopped answering.

**Run never globs a directory.** Which file to start comes from `Outputs` — the `Assembly` artifact,
and `RuntimeConfiguration` is how the project says it is startable at all. The responsibilities
document is explicit that a consumer must not "return an arbitrary `bin` DLL when several outputs
exist", and this is what having descriptors instead of a folder listing buys. It builds through
`ExecuteAsync` first and refuses to start anything if that failed, because running the previous
build after a failed one is how somebody ends up debugging code they did not write.

**A load context is rebuilt, not patched.** `ProjectAssemblyContext.IsCurrentFor` compares one
integer against the current snapshot; when it disagrees, the old context is dropped and a new one
built. The sample can dispose the old one immediately because it never hands a loaded type to
anything that outlives the swap. A real designer holds controls built from those assemblies and has
to let go of them first.

## What it is not

It does not render a preview. Turning a document into live Avalonia objects is `ArxisStudio.Markup`'s
work and needs a root-instance strategy this library deliberately does not guess at — see the
limitations. The XAML panel goes as far as the boundary honestly reaches: the assemblies the project
resolves to, the document's text fetched through the environment by the `avares` URI Avalonia would
name it with, and whatever parsing that text finds wrong — reported beside the project's own
diagnostics rather than in a list of its own, which is the reason the adapter translates them.

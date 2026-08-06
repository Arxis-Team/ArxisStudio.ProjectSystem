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

It needs `ArxisStudio.Markup` checked out beside this repository, because the adapter it uses does
([ADR 0018](../../docs/adr/0018-the-adapter-references-markup-by-source.md)).

## What each panel is a view of

| Panel | The API behind it |
| --- | --- |
| Solution explorer | `SolutionSnapshot.Projects`, `Folders`, `TryGetFolder`, and each project's `Items` |
| Project | every field of `ProjectSnapshot` — identity, evaluated context, surfaced properties |
| References | project, framework, assembly and analyzer references, with aliases |
| Outputs | `Outputs`, `GetRuntimeAssemblies`, and `EvaluationInputs` |
| Packages | `NuGetHttpFeed`, `PackageVersions.Latest`, `PackageInstaller.ApplyAndRestoreAsync` |
| Resolved | declared `PackageReference` items beside what restore actually resolved |
| XAML | `ProjectXamlEnvironment`, `ProjectAssemblyContext`, `ProjectResourceMap` |
| Toolbar | `LoadAsync`, `RefreshAsync`, `ExecuteAsync` for restore, build, rebuild and clean |
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

**A load context is rebuilt, not patched.** `ProjectAssemblyContext.IsCurrentFor` compares one
integer against the current snapshot; when it disagrees, the old context is dropped and a new one
built. The sample can dispose the old one immediately because it never hands a loaded type to
anything that outlives the swap. A real designer holds controls built from those assemblies and has
to let go of them first.

## What it is not

It does not render a preview. Turning a document into live Avalonia objects is `ArxisStudio.Markup`'s
work and needs a root-instance strategy this library deliberately does not guess at — see the
limitations. The XAML panel goes as far as the boundary honestly reaches: the assemblies the project
resolves to, and the document's text fetched through the environment by the `avares` URI Avalonia
would name it with.

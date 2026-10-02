using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// What a batch of file changes means: <see cref="SolutionSnapshot.Classify"/> — ADR 0025.
/// </summary>
/// <remarks>
/// The solution has what a designer's workspace has: a project with forms and code, its build
/// directories, a shared import, restore output that is not there yet, a library beside it, and a
/// project that lives under a directory whose name starts with a dot.
/// </remarks>
public sealed class ClassifyTests
{
    private static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    private static CanonicalPath Shared => TestPaths.At("src", "Directory.Build.props");
    private static CanonicalPath Form => TestPaths.At("src", "App", "Views", "MainWindow.axaml");
    private static CanonicalPath Code => TestPaths.At("src", "App", "Program.cs");
    private static CanonicalPath Assets => TestPaths.At("src", "App", "obj", "project.assets.json");
    private static CanonicalPath Linked => TestPaths.At("shared", "Common.cs");
    private static CanonicalPath Tool => TestPaths.At("src", ".tools", "Tool", "Tool.csproj");

    // Beside the app's project file: an import its evaluation reads and a None item its globs take.
    private static CanonicalPath Beside => TestPaths.At("src", "App", "Directory.Build.props");

    // Where a build of the app writes, as the evaluation names it.
    private static CanonicalPath IntermediateAssembly => TestPaths.At("src", "App", "obj", "Debug", "net10.0", "App.dll");
    private static CanonicalPath OutputFolder => TestPaths.At("src", "App", "bin", "Debug", "net10.0");

    [Fact]
    public void Classify_AnAtomicSaveOfAForm_IsOneEditedItemAndNothingToEvaluate()
    {
        // The two pure halves together: what Rider's safe write reports, netted, then classified.
        var batches = new List<ImmutableArray<FileChange>>();
        using FileChangeCoalescer coalescer = FileChangeCoalescer.ForChanges(batches.Add, null, new FakeTimeProvider());
        CanonicalPath temporary = TestPaths.At("src", "App", "Views", "MainWindow.axaml___jb_tmp___");
        CanonicalPath aside = TestPaths.At("src", "App", "Views", "MainWindow.axaml___jb_old___");

        coalescer.Add(new FileChange(temporary, FileChangeKind.Created));
        coalescer.Add(new FileChange(temporary, FileChangeKind.Changed));
        coalescer.Add(FileChange.Renamed(Form, aside));
        coalescer.Add(FileChange.Renamed(temporary, Form));
        coalescer.Add(new FileChange(aside, FileChangeKind.Deleted));
        coalescer.Flush();

        WorkspaceChangeSet changes = Solution().Classify(Assert.Single(batches));

        ProjectItemChange edited = Assert.Single(changes.ItemsEdited);
        Assert.Equal(Identity("App"), edited.Project);
        Assert.Equal(Form, edited.Item.FullPath);
        Assert.True(changes.Invalidation.IsEmpty);
        Assert.Empty(changes.MembershipChanged);
        Assert.Empty(changes.ProjectsToEvaluate);
    }

    [Fact]
    public void Classify_ANewFileInAProject_ChangesThatProjectsMembership()
    {
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("src", "App", "Views", "Settings.axaml"), FileChangeKind.Created)]);

        Assert.Equal([Identity("App")], changes.MembershipChanged);
        Assert.Equal([Identity("App")], changes.ProjectsToEvaluate);
        Assert.True(changes.Invalidation.IsEmpty);
        Assert.Empty(changes.ItemsEdited);
    }

    [Fact]
    public void Classify_ADeletedItem_ChangesItsProjectsMembership()
    {
        WorkspaceChangeSet changes = Solution().Classify([new FileChange(Code, FileChangeKind.Deleted)]);

        Assert.Equal([Identity("App")], changes.MembershipChanged);
    }

    [Theory]
    [InlineData("bin", "Debug", "net10.0", "App.dll")]
    [InlineData("obj", "Debug", "net10.0", "App.AssemblyInfo.cs")]
    [InlineData("bin", "ArxisStudio", "App.dll")]
    public void Classify_WhatABuildWrites_IsNothing(params string[] below)
    {
        CanonicalPath written = CanonicalPath.Create(TestPaths.Native(["src", "App", .. below]));

        WorkspaceChangeSet changes = Solution().Classify(
        [
            new FileChange(written, FileChangeKind.Created),
            new FileChange(written, FileChangeKind.Changed),
        ]);

        Assert.True(changes.IsEmpty, $"A build writing '{written}' is not the project changing: {changes}.");
        Assert.Same(WorkspaceChangeSet.None, changes);
    }

    [Fact]
    public void Classify_ABuildRewritingWhatTheEvaluationNamed_IsNothing()
    {
        // The MSBuild provider reports IntermediateAssembly and _OutputPathItem as items: what a build
        // writes, named by the evaluation that plans it, and rewritten by every build.
        SolutionSnapshot solution = Solution(withBuildItems: true);

        WorkspaceChangeSet changes = solution.Classify(
        [
            new FileChange(IntermediateAssembly, FileChangeKind.Changed),
            new FileChange(IntermediateAssembly, FileChangeKind.Created),
            new FileChange(OutputFolder, FileChangeKind.Deleted),
        ]);

        Assert.True(changes.IsEmpty, changes.ToString());
        Assert.False(solution.TryGetItem(IntermediateAssembly, out _, out _));
    }

    [Fact]
    public void Classify_AFileInADotDirectoryOfAProject_IsNothing()
    {
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("src", "App", ".vs", "App", "state.json"), FileChangeKind.Created)]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void Classify_ANewDotFileInAProject_ChangesItsMembership()
    {
        // SDK globs leave out directories whose names start with a dot, not files: an .editorconfig
        // beside the project is one of its None items.
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("src", "App", ".editorconfig"), FileChangeKind.Created)]);

        Assert.Equal([Identity("App")], changes.MembershipChanged);
    }

    [Fact]
    public void Classify_AProjectUnderADotDirectory_StillSeesItsOwnFiles()
    {
        // The rule about dots is about what is below a project, not where the project lives: a
        // project in a folder like .tools, or under a profile path with a dot in it, has files too.
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("src", ".tools", "Tool", "Helper.cs"), FileChangeKind.Created)]);

        Assert.Equal([Identity(Tool)], changes.MembershipChanged);
    }

    [Fact]
    public void Classify_AChangedImport_IsAnInvalidationOfTheProjectsThatReadIt()
    {
        WorkspaceChangeSet changes = Solution().Classify([new FileChange(Shared, FileChangeKind.Changed)]);

        Assert.Equal(WorkspaceInvalidationScope.Projects, changes.Invalidation.Scope);
        Assert.Equal([Identity("App"), Identity("Library")], changes.Invalidation.Projects);
        Assert.Empty(changes.ItemsEdited);
    }

    [Fact]
    public void Classify_RestoreOutputAppearing_IsAnInvalidation()
    {
        // Named by the snapshot though it is not there yet (ADR 0019): its appearing is the change.
        WorkspaceChangeSet changes = Solution().Classify([new FileChange(Assets, FileChangeKind.Created)]);

        Assert.Equal([Identity("App")], changes.Invalidation.Projects);
        Assert.Empty(changes.MembershipChanged);
    }

    [Fact]
    public void Classify_ARenamedItem_IsARenameAndAMembershipChange()
    {
        CanonicalPath renamed = TestPaths.At("src", "App", "Views", "ShellWindow.axaml");

        WorkspaceChangeSet changes = Solution().Classify([FileChange.Renamed(Form, renamed)]);

        Assert.Equal([new FileRename(Form, renamed)], changes.Renames);
        Assert.Equal([Identity("App")], changes.MembershipChanged);
    }

    [Fact]
    public void Classify_AFileMovedToAnotherProject_ChangesBothProjectsMembership()
    {
        CanonicalPath moved = TestPaths.At("src", "Library", "Program.cs");

        WorkspaceChangeSet changes = Solution().Classify([FileChange.Renamed(Code, moved)]);

        Assert.Equal([Identity("App"), Identity("Library")], changes.MembershipChanged);
        Assert.Equal([new FileRename(Code, moved)], changes.Renames);
    }

    [Fact]
    public void Classify_ARenamedFileNothingDeclares_IsNoRename()
    {
        WorkspaceChangeSet changes = Solution().Classify(
            [FileChange.Renamed(TestPaths.At("src", "App", "notes.txt"), TestPaths.At("src", "App", "todo.txt"))]);

        Assert.Empty(changes.Renames);
        Assert.Equal([Identity("App")], changes.MembershipChanged);
    }

    [Fact]
    public void Classify_ARenamedDirectory_RenamesEveryDeclaredFileInIt()
    {
        // A watcher reports the directory and nothing about what it held.
        CanonicalPath views = TestPaths.At("src", "App", "Views");
        CanonicalPath pages = TestPaths.At("src", "App", "Pages");

        WorkspaceChangeSet changes = Solution().Classify([FileChange.Renamed(views, pages)]);

        Assert.Equal([new FileRename(Form, TestPaths.At("src", "App", "Pages", "MainWindow.axaml"))], changes.Renames);
        Assert.Equal([Identity("App")], changes.MembershipChanged);
        Assert.True(changes.Invalidation.IsEmpty);
    }

    [Fact]
    public void Classify_AFileRenamedInsideARenamedDirectory_IsOneMoveFromWhereItWasToWhereItEnded()
    {
        CanonicalPath views = TestPaths.At("src", "App", "Views");
        CanonicalPath pages = TestPaths.At("src", "App", "Pages");
        CanonicalPath ended = TestPaths.At("src", "App", "Pages", "Shell.axaml");

        // The directory first, then the file in its new place.
        WorkspaceChangeSet directoryFirst = Solution().Classify(
        [
            FileChange.Renamed(views, pages),
            FileChange.Renamed(TestPaths.At("src", "App", "Pages", "MainWindow.axaml"), ended),
        ]);

        // The file first, then the directory it is in.
        WorkspaceChangeSet fileFirst = Solution().Classify(
        [
            FileChange.Renamed(Form, TestPaths.At("src", "App", "Views", "Shell.axaml")),
            FileChange.Renamed(views, pages),
        ]);

        Assert.Equal([new FileRename(Form, ended)], directoryFirst.Renames);
        Assert.Equal([new FileRename(Form, ended)], fileFirst.Renames);
    }

    [Fact]
    public void Classify_AFileRenamedThereAndBack_IsNoRename()
    {
        CanonicalPath aside = TestPaths.At("src", "App", "Views", "Aside.axaml");

        WorkspaceChangeSet changes = Solution().Classify(
            [FileChange.Renamed(Form, aside), FileChange.Renamed(aside, Form)]);

        Assert.Empty(changes.Renames);
    }

    [Fact]
    public void Classify_ADirectoryWhoseNameBeginsAProjectsDirectory_IsNothingOfThatProject()
    {
        // C:\src\Lib is a prefix of C:\src\Library as text and not as a path.
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("src", "Lib"), FileChangeKind.Deleted)]);

        Assert.True(changes.IsEmpty, changes.ToString());
    }

    [Fact]
    public void Classify_ADeletedDirectory_MakesStaleWhatReadAnythingInIt()
    {
        // A clean that removes obj takes the restore output with it.
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("src", "App", "obj"), FileChangeKind.Deleted)]);

        Assert.Equal([Identity("App")], changes.Invalidation.Projects);
        Assert.Equal([Assets], changes.Invalidation.Causes);
    }

    [Fact]
    public void Classify_ADeletedDirectoryOfLinkedFiles_ChangesTheMembershipOfEveryProjectDeclaringThem()
    {
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("shared"), FileChangeKind.Deleted)]);

        Assert.Equal([Identity("App"), Identity("Library")], changes.MembershipChanged);
    }

    [Fact]
    public void Classify_ARenamedDirectoryHoldingTheSolution_IsAnEntryPointChangeThatStillMovesItsFiles()
    {
        SolutionSnapshot solution = Solution(entryPoint: TestPaths.Solution());

        WorkspaceChangeSet changes = solution.Classify([FileChange.Renamed(TestPaths.At("src"), TestPaths.At("source"))]);

        Assert.Equal(WorkspaceInvalidationScope.EntryPoint, changes.Invalidation.Scope);
        Assert.Empty(changes.MembershipChanged);
        Assert.Contains(new FileRename(Form, TestPaths.At("source", "App", "Views", "MainWindow.axaml")), changes.Renames);
        Assert.DoesNotContain(changes.Renames, rename => rename.OldPath == Linked);
    }

    [Fact]
    public void Classify_AChangedImportThatIsAlsoAnItem_IsStaleAndEdited()
    {
        // Two questions with two answers: the evaluation that read it is stale, and whoever shows
        // the file has new text to show.
        WorkspaceChangeSet changes = Solution().Classify([new FileChange(Beside, FileChangeKind.Changed)]);

        Assert.Equal([Identity("App")], changes.Invalidation.Projects);
        Assert.Equal(Beside, Assert.Single(changes.ItemsEdited).Item.FullPath);
    }

    [Fact]
    public void Classify_AChangedDirectory_IsNothing()
    {
        // What a watcher says when a directory's listing is written: the structural change it
        // echoes has its own event.
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("src", "App", "Views"), FileChangeKind.Changed)]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void Classify_ARenameMissingAnEnd_IsTheEndItHas()
    {
        WorkspaceChangeSet changes = Solution().Classify(
        [
            new FileChange(TestPaths.At("src", "Library", "New.cs"), FileChangeKind.Renamed),
            new FileChange(CanonicalPath.None, FileChangeKind.Renamed) { OldPath = Code },
        ]);

        Assert.Equal([Identity("App"), Identity("Library")], changes.MembershipChanged);
        Assert.Empty(changes.Renames);
    }

    [Fact]
    public void Classify_AnOverflow_RequiresARescan()
    {
        WorkspaceChangeSet changes = Solution().Classify([FileChange.Overflow]);

        Assert.True(changes.RequiresRescan);
        Assert.False(changes.IsEmpty);
    }

    [Fact]
    public void Classify_AChangedFileNothingDeclares_IsNothing()
    {
        WorkspaceChangeSet changes = Solution().Classify(
            [new FileChange(TestPaths.At("src", "App", "notes.txt"), FileChangeKind.Changed)]);

        Assert.True(changes.IsEmpty);
    }

    [Fact]
    public void Classify_AProjectAlreadyStale_IsNotListedForMembershipToo()
    {
        WorkspaceChangeSet changes = Solution().Classify(
        [
            new FileChange(TestPaths.Project("App"), FileChangeKind.Changed),
            new FileChange(TestPaths.At("src", "App", "New.cs"), FileChangeKind.Created),
        ]);

        Assert.Equal([Identity("App")], changes.Invalidation.Projects);
        Assert.Empty(changes.MembershipChanged);
        Assert.Equal([Identity("App")], changes.ProjectsToEvaluate);
    }

    [Fact]
    public void Classify_AChangedSolution_ListsNothingElseToEvaluate()
    {
        SolutionSnapshot solution = Solution(entryPoint: TestPaths.Solution());

        WorkspaceChangeSet changes = solution.Classify(
        [
            new FileChange(TestPaths.Solution(), FileChangeKind.Changed),
            new FileChange(TestPaths.At("src", "App", "New.cs"), FileChangeKind.Created),
        ]);

        Assert.Equal(WorkspaceInvalidationScope.EntryPoint, changes.Invalidation.Scope);
        Assert.Empty(changes.MembershipChanged);
        Assert.Empty(changes.ProjectsToEvaluate);
    }

    [Fact]
    public void Classify_ALinkedItemDeletedOutsideEveryProjectDirectory_ChangesTheMembershipOfEveryProjectDeclaringIt()
    {
        // No project directory reaches it, so only the items say whose it was — and two projects
        // link it, so both lose it, not only the first.
        WorkspaceChangeSet changes = Solution().Classify([new FileChange(Linked, FileChangeKind.Deleted)]);

        Assert.Equal([Identity("App"), Identity("Library")], changes.MembershipChanged);
    }

    [Fact]
    public void Classify_ALinkedItemEdited_IsEditedOnceAsTheFirstProjectsItem()
    {
        WorkspaceChangeSet changes = Solution().Classify([new FileChange(Linked, FileChangeKind.Changed)]);

        Assert.Equal(Identity("App"), Assert.Single(changes.ItemsEdited).Project);
    }

    [Fact]
    public void Classify_AnItemEditedTwice_IsListedOnce()
    {
        WorkspaceChangeSet changes = Solution().Classify(
        [
            new FileChange(Code, FileChangeKind.Changed),
            new FileChange(Code, FileChangeKind.Changed),
        ]);

        Assert.Single(changes.ItemsEdited);
    }

    [Fact]
    public void Classify_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Solution().Classify(null!));
    }

    [Fact]
    public void TryGetItem_FindsTheItemAndTheProjectThatDeclaresIt()
    {
        Assert.True(Solution().TryGetItem(Form, out ProjectSnapshot? project, out ProjectItem? item));
        Assert.Equal(Identity("App"), project.Identity);
        Assert.Equal("AvaloniaXaml", item.ItemType);
    }

    [Fact]
    public void TryGetItem_AFileTwoProjectsDeclare_IsTheFirstProjects()
    {
        // The library links the same shared file; the app comes first in the snapshot.
        Assert.True(Solution().TryGetItem(Linked, out ProjectSnapshot? project, out _));
        Assert.Equal(Identity("App"), project.Identity);
    }

    [Fact]
    public void TryGetItem_AFileNothingDeclares_IsNotFound()
    {
        Assert.False(Solution().TryGetItem(TestPaths.At("src", "App", "notes.txt"), out ProjectSnapshot? project, out ProjectItem? item));
        Assert.Null(project);
        Assert.Null(item);
    }

    [Fact]
    public void WorkspaceChangeSet_ToString_SaysWhatTheBatchAmountsTo()
    {
        Assert.Equal("None", WorkspaceChangeSet.None.ToString());
        Assert.Equal(
            "1 item edited",
            Solution().Classify([new FileChange(Code, FileChangeKind.Changed)]).ToString());
    }

    private static ProjectIdentity Identity(string name) => Identity(TestPaths.Project(name));

    private static ProjectIdentity Identity(CanonicalPath projectFile) => ProjectIdentity.Create(Workspace, projectFile);

    private static SolutionSnapshot Solution(CanonicalPath entryPoint = default, bool withBuildItems = false)
    {
        var solution = new SolutionSnapshotBuilder
        {
            Workspace = Workspace,
            Name = "App",
            Request = new WorkspaceLoadRequest
            {
                Workspace = Workspace,
                EntryPointPath = entryPoint.IsEmpty ? TestPaths.Project("App") : entryPoint,
            },
        };

        ProjectSnapshotBuilder app = Project(TestPaths.Project("App"), "App");
        app.Items.Add(Item("Compile", Code));
        app.Items.Add(Item("AvaloniaXaml", Form));
        app.Items.Add(Item("Compile", Linked));
        app.Items.Add(Item("None", Beside));
        app.EvaluationInputs.Add(Assets);
        app.EvaluationInputs.Add(Beside);

        if (withBuildItems)
        {
            app.Items.Add(Item("IntermediateAssembly", IntermediateAssembly));
            app.Items.Add(Item("_OutputPathItem", OutputFolder));
        }
        solution.Projects.Add(app.ToSnapshot());

        ProjectSnapshotBuilder library = Project(TestPaths.Project("Library"), "Library");
        library.Items.Add(Item("Compile", TestPaths.At("src", "Library", "Class1.cs")));
        library.Items.Add(Item("Compile", Linked));
        solution.Projects.Add(library.ToSnapshot());

        ProjectSnapshotBuilder tool = Project(Tool, "Tool");
        tool.Items.Add(Item("Compile", TestPaths.At("src", ".tools", "Tool", "Tool.cs")));
        solution.Projects.Add(tool.ToSnapshot());

        return solution.ToSnapshot();
    }

    private static ProjectSnapshotBuilder Project(CanonicalPath projectFile, string name)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Identity(projectFile),
            Name = name,
            ProjectFilePath = projectFile,
        };

        project.EvaluationInputs.Add(projectFile);

        if (name != "Tool")
        {
            project.EvaluationInputs.Add(Shared);
        }

        project.BuildDirectories.Add(projectFile.Directory.Combine("bin"));
        project.BuildDirectories.Add(projectFile.Directory.Combine("obj"));

        return project;
    }

    private static ProjectItem Item(string type, CanonicalPath path) => new()
    {
        ItemType = type,
        Include = path.FileName,
        FullPath = path,
    };
}

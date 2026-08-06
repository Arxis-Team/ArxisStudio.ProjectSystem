using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.NuGet.Tests;

/// <summary>
/// Editing a project and restoring it as one operation.
/// </summary>
/// <remarks>
/// The restore is a fake provider rather than MSBuild, and that is the point rather than a
/// shortcut: this package must not be able to evaluate a project, so a test of it cannot need an
/// engine either. What is being tested is the ordering and the undo, and both are exact when the
/// restore's answer is whatever the test says it is.
/// </remarks>
public sealed class PackageInstallerTests : IDisposable
{
    private const string EmptyProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>

        """;

    private const string WithSerilog = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="Serilog" Version="4.1.0" />
          </ItemGroup>
        </Project>

        """;

    private const string CentralVersions = """
        <Project>
          <ItemGroup>
            <PackageVersion Include="Serilog" Version="4.1.0" />
          </ItemGroup>
        </Project>

        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "arxis-install-" + Guid.NewGuid().ToString("N"));

    public PackageInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AChangeThatRestores_LeavesTheProjectChanged()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);
        var provider = new RestoringProvider();

        ProjectOperationResult result = await Install(project, provider);

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("Serilog", await ReadAsync(project), StringComparison.Ordinal);

        Assert.Equal(ProjectOperationKind.Restore, provider.LastRequest!.Kind);
        Assert.Equal(project, provider.LastRequest.EntryPointPath);
    }

    /// <summary>
    /// What <c>dotnet add package</c> does, and for the same reason: a project carrying a reference
    /// that will not restore does not build, and whoever asked for the package is not better off for
    /// having half of it.
    /// </summary>
    [Fact]
    public async Task AChangeThatDoesNotRestore_IsPutBack()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);
        var provider = new RestoringProvider { Fail = "NU1102: no such version" };

        ProjectOperationResult result = await Install(project, provider);

        Assert.Equal(ProjectOperationStatus.Failed, result.Status);
        Assert.Equal(EmptyProject, await ReadAsync(project));

        // The restore's own diagnostic survives, because why it failed is the actionable part.
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("NU1102", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == PackageDiagnosticCodes.ChangeUndone);
    }

    /// <summary>Both files go back, not just the one the reference went into.</summary>
    [Fact]
    public async Task AFailedRestoreUnderCentralManagement_PutsBothFilesBack()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);
        CanonicalPath versions = Write("Directory.Packages.props", CentralVersions);

        var provider = new RestoringProvider { Fail = "NU1102" };

        ProjectOperationResult result = await Install(
            project, provider, layout: PackageVersionLayout.Central(versions), package: "Xunit");

        Assert.Equal(ProjectOperationStatus.Failed, result.Status);
        Assert.Equal(EmptyProject, await ReadAsync(project));
        Assert.Equal(CentralVersions, await ReadAsync(versions));
    }

    /// <summary>A restore for a file nobody touched is a wait nobody asked for.</summary>
    [Fact]
    public async Task AChangeWithNothingToDo_DoesNotRestore()
    {
        CanonicalPath project = Write("App.csproj", WithSerilog);
        var provider = new RestoringProvider();

        ProjectOperationResult result = await Install(project, provider);

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.Equal(PackageDiagnosticCodes.NothingToChange, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, provider.Restores);
        Assert.Equal(WithSerilog, await ReadAsync(project));
    }

    [Fact]
    public async Task AnEditThatFails_DoesNotRestore()
    {
        var provider = new RestoringProvider();

        ProjectOperationResult result = await Install(
            CanonicalPath.Create(Path.Combine(_root, "Nowhere.csproj")), provider);

        Assert.Equal(PackageDiagnosticCodes.ProjectFileNotFound, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, provider.Restores);
    }

    /// <summary>
    /// The restore changed what is on disk, not what the workspace knows, so nothing is published
    /// and the version does not move — ADR 0014, one level up.
    /// </summary>
    [Fact]
    public async Task Installing_PublishesNothing()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);
        var provider = new RestoringProvider();

        await using var workspace = new ProjectWorkspace(provider);

        int raised = 0;
        workspace.SnapshotChanged += (_, _) => raised++;

        await PackageInstaller.ApplyAndRestoreAsync(
            Request(project), workspace, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceVersion.None, workspace.CurrentVersion);
        Assert.Null(workspace.CurrentSnapshot);
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Routing is the workspace's, so a provider that cannot restore is an ordinary diagnostic and
    /// the change is put back exactly as for any other failure.
    /// </summary>
    [Fact]
    public async Task AWorkspaceThatCannotRestore_PutsTheChangeBack()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);

        await using var workspace = new ProjectWorkspace(new ReadingOnlyProvider());

        ProjectOperationResult result = await PackageInstaller.ApplyAndRestoreAsync(
            Request(project), workspace, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ProjectOperationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == ProjectDiagnosticCodes.UnsupportedOperation);
        Assert.Equal(EmptyProject, await ReadAsync(project));
    }

    [Fact]
    public async Task Progress_ReachesTheCaller()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);
        var provider = new RestoringProvider { Report = "restoring App" };
        var seen = new RecordingProgress();

        await using var workspace = new ProjectWorkspace(provider);

        await PackageInstaller.ApplyAndRestoreAsync(
            Request(project),
            workspace,
            layout: null,
            seen,
            TestContext.Current.CancellationToken);

        Assert.Contains("restoring App", seen.Messages);
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        await using var workspace = new ProjectWorkspace(new RestoringProvider());

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await PackageInstaller.ApplyAndRestoreAsync(
                null!, workspace, cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await PackageInstaller.ApplyAndRestoreAsync(
                Request(TestProject()), null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACancelledInstall_Throws()
    {
        await using var workspace = new ProjectWorkspace(new RestoringProvider());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PackageInstaller.ApplyAndRestoreAsync(
                Request(TestProject()), workspace, cancellationToken: cancellation.Token));
    }

    private static async ValueTask<ProjectOperationResult> Install(
        CanonicalPath project,
        RestoringProvider provider,
        PackageVersionLayout? layout = null,
        string package = "Serilog")
    {
        await using var workspace = new ProjectWorkspace(provider);

        return await PackageInstaller.ApplyAndRestoreAsync(
            Request(project, package),
            workspace,
            layout,
            progress: null,
            TestContext.Current.CancellationToken);
    }

    private static PackageEditRequest Request(CanonicalPath project, string package = "Serilog") => new()
    {
        Kind = PackageEditKind.Install,
        ProjectFilePath = project,
        PackageId = package,
        Version = "4.1.0",
    };

    private CanonicalPath TestProject() => CanonicalPath.Create(Path.Combine(_root, "App.csproj"));

    private CanonicalPath Write(string name, string content)
    {
        string path = Path.Combine(_root, name);

        File.WriteAllText(path, content.ReplaceLineEndings("\n"));

        return CanonicalPath.Create(path);
    }

    private static async Task<string> ReadAsync(CanonicalPath path) =>
        (await File.ReadAllTextAsync(path.Value, TestContext.Current.CancellationToken))
            .ReplaceLineEndings("\n");

    /// <summary>A provider that restores, and says what the test told it to say.</summary>
    private sealed class RestoringProvider : IProjectSystemProvider, IProjectOperationProvider
    {
        public string Name => "Restoring";

        public string? Fail { get; init; }

        public string? Report { get; init; }

        public int Restores { get; private set; }

        public ProjectOperationRequest? LastRequest { get; private set; }

        public bool CanLoad(WorkspaceEntryPoint entryPoint) => true;

        public ValueTask<WorkspaceLoadResult> LoadAsync(
            WorkspaceLoadRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This provider exists to restore.");

        public bool CanExecute(ProjectOperationKind kind) => kind == ProjectOperationKind.Restore;

        public ValueTask<ProjectOperationResult> ExecuteAsync(
            ProjectOperationRequest request,
            IProgress<ProjectOperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            Restores++;
            LastRequest = request;

            if (Report is not null)
            {
                progress?.Report(new ProjectOperationProgress { Message = Report });
            }

            return ValueTask.FromResult(Fail is null
                ? ProjectOperationResult.Succeeded()
                : ProjectOperationResult.Failed(
                    new ProjectDiagnostic("NU1102", Fail, ProjectDiagnosticSeverity.Error)));
        }
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that records on the thread that reports.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Progress{T}"/>, which posts to the captured synchronization context or, with
    /// none, to the thread pool: the callback then runs at some point after the operation returns,
    /// and a test can only wait and hope. The interface makes no such promise, the installer simply
    /// calls <c>Report</c>, and recording inline turns "has it arrived yet" into a question that is
    /// already answered by the time the await completes.
    /// </remarks>
    private sealed class RecordingProgress : IProgress<ProjectOperationProgress>
    {
        private readonly List<string> _messages = [];

        /// <summary>What was reported, in order.</summary>
        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public void Report(ProjectOperationProgress value)
        {
            lock (_messages)
            {
                _messages.Add(value.Message);
            }
        }
    }

    /// <summary>A provider that reads projects and cannot do anything to them.</summary>
    private sealed class ReadingOnlyProvider : IProjectSystemProvider
    {
        public string Name => "ReadingOnly";

        public bool CanLoad(WorkspaceEntryPoint entryPoint) => true;

        public ValueTask<WorkspaceLoadResult> LoadAsync(
            WorkspaceLoadRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This provider exists to decline operations.");
    }
}

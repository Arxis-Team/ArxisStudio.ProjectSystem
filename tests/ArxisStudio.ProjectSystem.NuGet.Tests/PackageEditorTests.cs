using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArxisStudio.ProjectSystem.NuGet.Tests;

/// <summary>
/// Installing, updating and removing packages, against real files.
/// </summary>
/// <remarks>
/// The XML decisions are settled in <see cref="PackageReferenceRewriterTests"/> without a disk.
/// What is left is reading, writing and putting back — which is about files, so these use some.
/// Nothing here restores, evaluates or reaches a network.
/// </remarks>
public sealed class PackageEditorTests : IDisposable
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
        Path.GetTempPath(), "arxis-edit-" + Guid.NewGuid().ToString("N"));

    public PackageEditorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Installing_AddsTheReferenceWithItsVersion()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);

        ProjectOperationResult result = await Apply(PackageEditKind.Install, project, "Serilog", "4.1.0");

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>

            """, await ReadAsync(project));
    }

    [Fact]
    public async Task Updating_ChangesOnlyTheVersion()
    {
        CanonicalPath project = Write("App.csproj", WithSerilog);

        ProjectOperationResult result = await Apply(PackageEditKind.Update, project, "Serilog", "5.0.0");

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.Contains("""Version="5.0.0" """.TrimEnd(), await ReadAsync(project), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstalling_TakesTheReferenceAndTheEmptyGroup()
    {
        CanonicalPath project = Write("App.csproj", WithSerilog);

        ProjectOperationResult result = await Apply(PackageEditKind.Uninstall, project, "Serilog");

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
            </Project>

            """, await ReadAsync(project));
    }

    /// <summary>
    /// A no-op looks exactly like a successful change to a caller that only checks the status, so it
    /// is said out loud — as a warning, because nothing went wrong.
    /// </summary>
    [Theory]
    [InlineData(PackageEditKind.Install, "Serilog")]
    [InlineData(PackageEditKind.Update, "Absent")]
    [InlineData(PackageEditKind.Uninstall, "Absent")]
    public async Task AnEditWithNothingToDo_SaysSo(PackageEditKind kind, string package)
    {
        CanonicalPath project = Write("App.csproj", WithSerilog);

        ProjectOperationResult result = await Apply(kind, project, package, "1.0.0");

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);

        ProjectDiagnostic diagnostic = Assert.Single(result.Diagnostics);

        Assert.Equal(PackageDiagnosticCodes.NothingToChange, diagnostic.Code);
        Assert.Equal(ProjectDiagnosticSeverity.Warning, diagnostic.Severity);

        Assert.Equal(WithSerilog, await ReadAsync(project));
    }

    /// <summary>
    /// Under central management the project carries the reference and the versions file carries the
    /// version. Writing a version onto the reference as well is an error NuGet reports rather than a
    /// preference it honours.
    /// </summary>
    [Fact]
    public async Task InstallingUnderCentralManagement_SplitsTheChangeAcrossTwoFiles()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);
        CanonicalPath versions = Write("Directory.Packages.props", CentralVersions);

        ProjectOperationResult result = await Apply(
            PackageEditKind.Install, project, "Xunit", "3.2.2", PackageVersionLayout.Central(versions));

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);

        Assert.Contains("""<PackageReference Include="Xunit" />""", await ReadAsync(project), StringComparison.Ordinal);
        Assert.DoesNotContain("Version=", await ReadAsync(project), StringComparison.Ordinal);

        Assert.Contains(
            """<PackageVersion Include="Xunit" Version="3.2.2" />""",
            await ReadAsync(versions),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatingUnderCentralManagement_ChangesTheVersionsFileAndNotTheProject()
    {
        CanonicalPath project = Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>

            """);

        CanonicalPath versions = Write("Directory.Packages.props", CentralVersions);
        string before = await ReadAsync(project);

        ProjectOperationResult result = await Apply(
            PackageEditKind.Update, project, "Serilog", "5.0.0", PackageVersionLayout.Central(versions));

        Assert.Equal(ProjectOperationStatus.Succeeded, result.Status);
        Assert.Equal(before, await ReadAsync(project));

        Assert.Contains(
            """<PackageVersion Include="Serilog" Version="5.0.0" />""",
            await ReadAsync(versions),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The central version may still be pinning the package for another project, and this editor
    /// was given one project and cannot see the others. Removing it would break them silently.
    /// </summary>
    [Fact]
    public async Task UninstallingUnderCentralManagement_LeavesTheCentralVersionAlone()
    {
        CanonicalPath project = Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>

            """);

        CanonicalPath versions = Write("Directory.Packages.props", CentralVersions);

        await Apply(PackageEditKind.Uninstall, project, "Serilog", layout: PackageVersionLayout.Central(versions));

        Assert.DoesNotContain("PackageReference", await ReadAsync(project), StringComparison.Ordinal);
        Assert.Equal(CentralVersions, await ReadAsync(versions));
    }

    /// <summary>
    /// The reason this is a transaction at all. Two files must change together, and a half-applied
    /// edit leaves a repository that does not restore: a reference with no version.
    /// </summary>
    [Fact]
    public async Task WhenTheSecondFileCannotBeWritten_TheFirstIsPutBack()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);
        CanonicalPath versions = Write("Directory.Packages.props", CentralVersions);

        // Held open for writing, which is what an editor with unsaved changes does. The project is
        // written first and must be undone when this one fails.
        using (new FileStream(versions.Value, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ProjectOperationResult result = await Apply(
                PackageEditKind.Install, project, "Xunit", "3.2.2", PackageVersionLayout.Central(versions));

            Assert.Equal(ProjectOperationStatus.Failed, result.Status);
            Assert.Equal(PackageDiagnosticCodes.FileNotWritable, Assert.Single(result.Diagnostics).Code);
        }

        Assert.Equal(EmptyProject, await ReadAsync(project));
        Assert.Equal(CentralVersions, await ReadAsync(versions));
    }

    [Fact]
    public async Task AMissingProject_IsADiagnostic()
    {
        ProjectOperationResult result = await Apply(
            PackageEditKind.Install,
            CanonicalPath.Create(Path.Combine(_root, "Nowhere.csproj")),
            "Serilog",
            "4.1.0");

        Assert.Equal(PackageDiagnosticCodes.ProjectFileNotFound, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task AMissingCentralVersionsFile_IsADiagnostic()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);

        ProjectOperationResult result = await Apply(
            PackageEditKind.Install,
            project,
            "Serilog",
            "4.1.0",
            PackageVersionLayout.Central(CanonicalPath.Create(Path.Combine(_root, "Nowhere.props"))));

        Assert.Equal(PackageDiagnosticCodes.CentralVersionFileNotFound, Assert.Single(result.Diagnostics).Code);
    }

    /// <summary>
    /// Reported rather than repaired. A project somebody has broken by hand is theirs to fix, and
    /// rewriting it from a partial parse would lose whatever else is in it.
    /// </summary>
    [Fact]
    public async Task AMalformedProject_IsADiagnostic()
    {
        CanonicalPath project = Write("App.csproj", "<Project><ItemGroup></Project>");

        ProjectOperationResult result = await Apply(PackageEditKind.Install, project, "Serilog", "4.1.0");

        Assert.Equal(PackageDiagnosticCodes.FileNotWellFormed, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task AnEditThatChangesNothing_DoesNotRewriteTheFile()
    {
        CanonicalPath project = Write("App.csproj", WithSerilog);
        DateTime before = File.GetLastWriteTimeUtc(project.Value);

        await Apply(PackageEditKind.Update, project, "Serilog", "4.1.0");

        Assert.Equal(before, File.GetLastWriteTimeUtc(project.Value));
    }

    /// <summary>
    /// A repository that normalises to LF is not made an exception to its own rule by being edited
    /// on Windows.
    /// </summary>
    [Fact]
    public async Task AFileWrittenWithOneNewline_KeepsIt()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject.ReplaceLineEndings("\r\n"));

        await Apply(PackageEditKind.Install, project, "Serilog", "4.1.0");

        string updated = await ReadAsync(project);

        Assert.DoesNotContain(updated.Replace("\r\n", string.Empty, StringComparison.Ordinal), "\n", StringComparison.Ordinal);
    }

    /// <summary>Legacy project files carry one, and losing it would be a change nobody asked for.</summary>
    [Fact]
    public async Task AFileWithAnXmlDeclaration_KeepsIt()
    {
        CanonicalPath project = Write("App.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.NET.Sdk">
            </Project>

            """);

        await Apply(PackageEditKind.Install, project, "Serilog", "4.1.0");

        // No blank line before the group: there was nothing above it to be separated from.
        Assert.Equal("""
            <?xml version="1.0" encoding="utf-8"?>
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>

            """, await ReadAsync(project));
    }

    [Fact]
    public async Task ARequestWithoutAVersion_Throws()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await PackageEditor.ApplyAsync(
                new PackageEditRequest
                {
                    Kind = PackageEditKind.Install,
                    ProjectFilePath = project,
                    PackageId = "Serilog",
                },
                layout: null,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ARequestWithoutAProjectOrAPackage_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PackageEditRequest
        {
            Kind = PackageEditKind.Install,
            ProjectFilePath = CanonicalPath.None,
            PackageId = "Serilog",
        });

        Assert.Throws<ArgumentException>(() => new PackageEditRequest
        {
            Kind = PackageEditKind.Install,
            ProjectFilePath = CanonicalPath.Create(Path.Combine(_root, "App.csproj")),
            PackageId = "  ",
        });
    }

    [Fact]
    public async Task ACancelledEdit_Throws()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PackageEditor.ApplyAsync(
                new PackageEditRequest
                {
                    Kind = PackageEditKind.Install,
                    ProjectFilePath = project,
                    PackageId = "Serilog",
                    Version = "4.1.0",
                },
                layout: null,
                cancellation.Token));
    }

    [Fact]
    public async Task InstallingWithAssetMetadata_WritesIt()
    {
        CanonicalPath project = Write("App.csproj", EmptyProject);

        await PackageEditor.ApplyAsync(
            new PackageEditRequest
            {
                Kind = PackageEditKind.Install,
                ProjectFilePath = project,
                PackageId = "StyleCop.Analyzers",
                Version = "1.2.0",
                PrivateAssets = "all",
                ExcludeAssets = "runtime",
            },
            layout: null,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            """<PackageReference Include="StyleCop.Analyzers" Version="1.2.0" PrivateAssets="all" ExcludeAssets="runtime" />""",
            await ReadAsync(project),
            StringComparison.Ordinal);
    }

    private static ValueTask<ProjectOperationResult> Apply(
        PackageEditKind kind,
        CanonicalPath project,
        string package,
        string? version = null,
        PackageVersionLayout? layout = null) =>
        PackageEditor.ApplyAsync(
            new PackageEditRequest
            {
                Kind = kind,
                ProjectFilePath = project,
                PackageId = package,
                Version = version,
            },
            layout,
            TestContext.Current.CancellationToken);

    private CanonicalPath Write(string name, string content)
    {
        string path = Path.Combine(_root, name);

        File.WriteAllText(path, content.ReplaceLineEndings("\n"));

        return CanonicalPath.Create(path);
    }

    private static async Task<string> ReadAsync(CanonicalPath path) =>
        (await File.ReadAllTextAsync(path.Value, TestContext.Current.CancellationToken))
            .ReplaceLineEndings("\n");
}

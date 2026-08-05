using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// Reading what restore resolved.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here restores anything. Unlike MSBuild evaluation, this is a pure read of a documented
/// file, so the file is written by the test — which keeps the package cache and the network out of
/// the suite exactly as the contract requires, and lets every case state precisely the input it is
/// about.
/// </para>
/// <para>
/// The package root is built for whichever platform the tests run on, because an absolute path is
/// the one thing a real assets file contains that cannot be written down portably.
/// </para>
/// </remarks>
public sealed class RestoreAssetsReaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aps-assets-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static string PackageRoot => OperatingSystem.IsWindows() ? "C:\\\\packages\\\\" : "/packages/";

    private static CanonicalPath ExpectedRoot =>
        CanonicalPath.Create(OperatingSystem.IsWindows() ? "C:\\packages" : "/packages");

    private CanonicalPath Write(string json)
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, RestoreAssetsReader.FileName);

        File.WriteAllText(path, json);

        return CanonicalPath.Create(path);
    }

    private ImmutableArray<ResolvedPackage> Read(string json, string? framework = "net10.0")
    {
        Assert.True(
            RestoreAssetsReader.TryRead(Write(json), framework, out ImmutableArray<ResolvedPackage> packages, out string? error),
            $"The assets file could not be read: {error}");

        return packages;
    }

    [Fact]
    public void APackage_ContributesItsCompileAndRuntimeAssemblies()
    {
        ResolvedPackage package = Assert.Single(Read($$"""
        {
          "version": 4,
          "targets": { "net10.0": { "Serilog/4.1.0": {
              "type": "package",
              "compile": { "lib/net8.0/Serilog.dll": {} },
              "runtime": { "lib/net8.0/Serilog.dll": {} } } } },
          "libraries": { "Serilog/4.1.0": { "type": "package", "path": "serilog/4.1.0" } },
          "projectFileDependencyGroups": { "net10.0": [ "Serilog >= 4.1.0" ] },
          "packageFolders": { "{{PackageRoot}}": {} }
        }
        """));

        Assert.Equal("Serilog", package.PackageId);
        Assert.Equal("4.1.0", package.Version);
        Assert.True(package.IsDirect);

        CanonicalPath expected = CanonicalPath.Create(ExpectedRoot, "serilog/4.1.0/lib/net8.0/Serilog.dll");

        Assert.Equal([expected], package.CompileAssemblies);
        Assert.Equal([expected], package.RuntimeAssemblies);
    }

    /// <summary>
    /// Restore's marker for "this package contributes nothing of that kind". Every
    /// <c>ExcludeAssets="runtime"</c> produces one, including six of the ten in this repository's own
    /// assets file, so taking it literally would hand out paths to a file that is empty by design.
    /// </summary>
    [Fact]
    public void APlaceholderAsset_IsNotAnAssembly()
    {
        ResolvedPackage package = Assert.Single(Read($$"""
        {
          "version": 4,
          "targets": { "net10.0": { "Microsoft.Build/18.8.2": {
              "type": "package",
              "compile": { "ref/net10.0/Microsoft.Build.dll": {} },
              "runtime": { "lib/net10.0/_._": {} } } } },
          "libraries": { "Microsoft.Build/18.8.2": { "type": "package", "path": "microsoft.build/18.8.2" } },
          "packageFolders": { "{{PackageRoot}}": {} }
        }
        """));

        Assert.Single(package.CompileAssemblies);
        Assert.Empty(package.RuntimeAssemblies);
        Assert.False(package.RuntimeAssemblies.IsDefault);
    }

    /// <summary>
    /// A project entry's asset is <c>bin/placeholder/Name.dll</c>, which never exists. The real
    /// output comes from evaluating that project, which the provider already does — so treating it
    /// as a package would invent a path and duplicate a reference.
    /// </summary>
    [Fact]
    public void AProjectEntry_IsNotAPackage()
    {
        Assert.Empty(Read($$"""
        {
          "version": 4,
          "targets": { "net10.0": { "Core/1.0.0": {
              "type": "project",
              "compile": { "bin/placeholder/Core.dll": {} },
              "runtime": { "bin/placeholder/Core.dll": {} } } } },
          "libraries": { "Core/1.0.0": { "type": "project", "path": "../Core/Core.csproj" } },
          "packageFolders": { "{{PackageRoot}}": {} }
        }
        """));
    }

    [Fact]
    public void ATransitivePackage_IsListedAndMarkedIndirect()
    {
        ImmutableArray<ResolvedPackage> packages = Read($$"""
        {
          "version": 4,
          "targets": { "net10.0": {
            "Serilog/4.1.0": { "type": "package", "dependencies": { "Serilog.Core": "4.1.0" } },
            "Serilog.Core/4.1.0": { "type": "package" } } },
          "libraries": {
            "Serilog/4.1.0": { "type": "package", "path": "serilog/4.1.0" },
            "Serilog.Core/4.1.0": { "type": "package", "path": "serilog.core/4.1.0" } },
          "projectFileDependencyGroups": { "net10.0": [ "Serilog >= 4.1.0" ] },
          "packageFolders": { "{{PackageRoot}}": {} }
        }
        """);

        Assert.Equal(2, packages.Length);

        ResolvedPackage direct = packages.Single(static p => p.PackageId == "Serilog");
        ResolvedPackage transitive = packages.Single(static p => p.PackageId == "Serilog.Core");

        Assert.True(direct.IsDirect);
        Assert.Equal(["Serilog.Core"], direct.Dependencies);
        Assert.False(transitive.IsDirect);
        Assert.Empty(transitive.Dependencies);
    }

    /// <summary>
    /// A restore for a specific runtime writes <c>net10.0/win-x64</c>, so matching only the exact
    /// framework would silently find nothing on a project that names one.
    /// </summary>
    [Fact]
    public void ARuntimeQualifiedTarget_IsFoundByItsFramework()
    {
        Assert.Single(Read($$"""
        {
          "version": 4,
          "targets": { "net10.0/win-x64": { "Serilog/4.1.0": { "type": "package" } } },
          "libraries": { "Serilog/4.1.0": { "type": "package", "path": "serilog/4.1.0" } },
          "packageFolders": { "{{PackageRoot}}": {} }
        }
        """));
    }

    [Fact]
    public void AFrameworkRestoreDidNotProduce_IsEmptyRatherThanAnError()
    {
        Assert.Empty(Read($$"""
        {
          "version": 4,
          "targets": { "net8.0": { "Serilog/4.1.0": { "type": "package" } } },
          "libraries": { "Serilog/4.1.0": { "type": "package", "path": "serilog/4.1.0" } },
          "packageFolders": { "{{PackageRoot}}": {} }
        }
        """));
    }

    /// <summary>A single-target project's one entry is unambiguous; two would be a guess.</summary>
    [Fact]
    public void WithNoFrameworkAsked_ASingleTargetIsUsedAndSeveralAreNot()
    {
        const string OneTarget = """
        {
          "targets": { "net10.0": { "Serilog/4.1.0": { "type": "package" } } },
          "libraries": { "Serilog/4.1.0": { "type": "package", "path": "serilog/4.1.0" } }
        }
        """;

        Assert.Single(Read(OneTarget, framework: null));

        const string TwoTargets = """
        {
          "targets": {
            "net10.0": { "Serilog/4.1.0": { "type": "package" } },
            "net8.0": { "Serilog/4.1.0": { "type": "package" } } },
          "libraries": { "Serilog/4.1.0": { "type": "package", "path": "serilog/4.1.0" } }
        }
        """;

        Assert.Empty(Read(TwoTargets, framework: null));
    }

    [Fact]
    public void WithoutAPackageRoot_ThePackageIsStillListedWithoutAssemblies()
    {
        ResolvedPackage package = Assert.Single(Read("""
        {
          "targets": { "net10.0": { "Serilog/4.1.0": {
              "type": "package",
              "compile": { "lib/net8.0/Serilog.dll": {} } } } },
          "libraries": { "Serilog/4.1.0": { "type": "package", "path": "serilog/4.1.0" } }
        }
        """));

        Assert.Equal("Serilog", package.PackageId);
        Assert.Empty(package.CompileAssemblies);
    }

    [Fact]
    public void AMalformedAssetsFile_IsReportedRatherThanThrown()
    {
        Assert.False(
            RestoreAssetsReader.TryRead(Write("{ not json"), "net10.0", out ImmutableArray<ResolvedPackage> packages, out string? error));

        Assert.Empty(packages);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AMissingAssetsFile_IsReportedRatherThanThrown()
    {
        Directory.CreateDirectory(_directory);

        Assert.False(RestoreAssetsReader.TryRead(
            CanonicalPath.Create(Path.Combine(_directory, "absent.json")), "net10.0", out _, out string? error));

        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// A future assets version is read on the assumption that the parts used here are unchanged,
    /// because they have been through every version so far. Failing outright on an unknown number
    /// would break the day a new SDK ships.
    /// </summary>
    [Fact]
    public void AnUnfamiliarVersion_IsReadAnyway()
    {
        Assert.Single(Read($$"""
        {
          "version": 99,
          "targets": { "net10.0": { "Serilog/4.1.0": { "type": "package" } } },
          "libraries": { "Serilog/4.1.0": { "type": "package", "path": "serilog/4.1.0" } },
          "packageFolders": { "{{PackageRoot}}": {} }
        }
        """));
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// Turning "watch these files" into "watch these directories".
/// </summary>
/// <remarks>
/// The existence check is a parameter, so every rule here is decided without a disk and asserted
/// exactly. Which directories exist is the only thing that varies, and a test says which.
/// </remarks>
public sealed class FileWatchPlanTests
{
    private static string Root => OperatingSystem.IsWindows() ? "C:\\" : "/";

    private static CanonicalPath At(params string[] segments) =>
        CanonicalPath.Create(Root + string.Join(System.IO.Path.DirectorySeparatorChar, segments));

    [Fact]
    public void AFile_IsWatchedThroughItsDirectory()
    {
        ImmutableArray<WatchedDirectory> plan = Plan(
            [At("src", "App", "App.csproj")],
            exists: [At("src", "App")]);

        WatchedDirectory watched = Assert.Single(plan);

        Assert.Equal(At("src", "App"), watched.Directory);
        Assert.False(watched.IncludeSubdirectories);
    }

    [Fact]
    public void SeveralFilesInOneDirectory_AreOneWatch()
    {
        ImmutableArray<WatchedDirectory> plan = Plan(
            [At("src", "Directory.Build.props"), At("src", "Directory.Packages.props")],
            exists: [At("src")]);

        Assert.Equal(At("src"), Assert.Single(plan).Directory);
    }

    [Fact]
    public void FilesInDifferentDirectories_AreSeparateWatches()
    {
        ImmutableArray<WatchedDirectory> plan = Plan(
            [At("src", "App", "App.csproj"), At("src", "Library", "Library.csproj")],
            exists: [At("src", "App"), At("src", "Library")]);

        Assert.Equal([At("src", "App"), At("src", "Library")], plan.Select(static w => w.Directory));
        Assert.All(plan, static w => Assert.False(w.IncludeSubdirectories));
    }

    /// <summary>
    /// The case this rule exists for: an unrestored project names its assets file and has no
    /// <c>obj</c>. Without the ancestor watch, a restore finishing — the change that most needs
    /// noticing — would be the one that never arrived.
    /// </summary>
    [Fact]
    public void AFileInADirectoryThatIsNotThere_IsWatchedThroughTheNearestOneThatIs()
    {
        ImmutableArray<WatchedDirectory> plan = Plan(
            [At("src", "App", "obj", "project.assets.json")],
            exists: [At("src", "App"), At("src")]);

        WatchedDirectory watched = Assert.Single(plan);

        Assert.Equal(At("src", "App"), watched.Directory);
        Assert.True(watched.IncludeSubdirectories);
    }

    [Fact]
    public void AMissingDirectorySeveralLevelsDeep_WalksUpUntilSomethingExists()
    {
        ImmutableArray<WatchedDirectory> plan = Plan(
            [At("src", "App", "obj", "Debug", "net10.0", "generated.props")],
            exists: [At("src")]);

        Assert.Equal(At("src"), Assert.Single(plan).Directory);
    }

    /// <summary>
    /// The ordinary shape of a real project: the project file beside a missing <c>obj</c>. One
    /// directory, watched once, and recursively — because the narrower watch would not see inside
    /// the directory that is about to appear.
    /// </summary>
    [Fact]
    public void ADirectoryThatIsBothAParentAndAnAncestor_IsWatchedOnceAndWidely()
    {
        ImmutableArray<WatchedDirectory> plan = Plan(
            [At("src", "App", "App.csproj"), At("src", "App", "obj", "project.assets.json")],
            exists: [At("src", "App")]);

        WatchedDirectory watched = Assert.Single(plan);

        Assert.Equal(At("src", "App"), watched.Directory);
        Assert.True(watched.IncludeSubdirectories);
    }

    /// <summary>A recursive watch already covers what is beneath it; watching both reports twice.</summary>
    [Fact]
    public void ADirectoryUnderARecursiveWatch_IsNotWatchedAgain()
    {
        ImmutableArray<WatchedDirectory> plan = Plan(
            [
                At("src", "App", "obj", "project.assets.json"),
                At("src", "App", "nested", "Directory.Build.props"),
            ],
            exists: [At("src", "App"), At("src", "App", "nested")]);

        WatchedDirectory watched = Assert.Single(plan);

        Assert.Equal(At("src", "App"), watched.Directory);
        Assert.True(watched.IncludeSubdirectories);
    }

    [Fact]
    public void EmptyPaths_AreIgnored()
    {
        Assert.Empty(Plan([CanonicalPath.None], exists: []));
        Assert.Empty(Plan([], exists: []));
    }

    /// <summary>
    /// A path whose directory exists nowhere up to the root — an unplugged drive, a share that has
    /// gone. Nothing to watch is the answer, rather than an exception thrown at load time.
    /// </summary>
    [Fact]
    public void APathWithNoExistingAncestor_IsWatchedNowhere() =>
        Assert.Empty(Plan([At("gone", "App", "App.csproj")], exists: []));

    private static ImmutableArray<WatchedDirectory> Plan(
        IEnumerable<CanonicalPath> paths,
        IEnumerable<CanonicalPath> exists)
    {
        var existing = new HashSet<CanonicalPath>(exists);

        return FileWatchPlan.For(paths, existing.Contains);
    }
}

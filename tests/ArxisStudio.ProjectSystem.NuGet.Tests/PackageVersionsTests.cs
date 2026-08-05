using System;
using System.Collections.Immutable;
using Xunit;

namespace ArxisStudio.ProjectSystem.NuGet.Tests;

/// <summary>
/// Ordering and choosing among versions.
/// </summary>
/// <remarks>
/// The cases here are the ones that make hand-rolling SemVer a mistake: a prerelease sorting before
/// the release it precedes, a fourth numeric field, prerelease identifiers comparing numerically or
/// lexically depending on their shape, and build metadata being ignored entirely.
/// </remarks>
public sealed class PackageVersionsTests
{
    [Theory]
    [InlineData("1.0.0-beta", true)]
    [InlineData("1.0.0-rc.1", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0+build.5", false)]
    [InlineData("not a version", false)]
    [InlineData(null, false)]
    public void IsPrerelease_ReadsTheLabel(string? version, bool expected) =>
        Assert.Equal(expected, PackageVersions.IsPrerelease(version));

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.0", true)]
    [InlineData("1.0.0.4", true)]
    [InlineData("1.0.0-beta.2+sha.abc", true)]
    [InlineData("", false)]
    [InlineData("banana", false)]
    public void IsValid_SaysWhetherItParses(string version, bool expected) =>
        Assert.Equal(expected, PackageVersions.IsValid(version));

    /// <summary>The rule that catches out every hand-rolled comparison.</summary>
    [Fact]
    public void APrerelease_SortsBeforeTheReleaseItPrecedes()
    {
        Assert.True(PackageVersions.Compare("1.0.0-beta", "1.0.0") < 0);
        Assert.True(PackageVersions.Compare("2.0.0-rc.1", "2.0.0") < 0);
        Assert.True(PackageVersions.Compare("1.0.0", "1.0.1-alpha") < 0);
    }

    [Fact]
    public void PrereleaseIdentifiers_CompareNumericallyWhenTheyAreNumbers()
    {
        // A lexical comparison would put beta.10 before beta.9.
        Assert.True(PackageVersions.Compare("1.0.0-beta.9", "1.0.0-beta.10") < 0);

        // And an alphanumeric identifier outranks a numeric one at the same position.
        Assert.True(PackageVersions.Compare("1.0.0-alpha.1", "1.0.0-alpha.beta") < 0);
    }

    [Fact]
    public void BuildMetadata_IsIgnored() =>
        Assert.Equal(0, PackageVersions.Compare("1.0.0+sha.aaa", "1.0.0+sha.bbb"));

    /// <summary>NuGet's fourth field, which SemVer itself does not have.</summary>
    [Fact]
    public void AFourthField_Counts()
    {
        Assert.True(PackageVersions.Compare("1.0.0", "1.0.0.1") < 0);
        Assert.Equal(0, PackageVersions.Compare("1.0", "1.0.0"));
    }

    [Fact]
    public void ComparingSomethingThatIsNotAVersion_Throws()
    {
        Assert.Throws<ArgumentException>(() => PackageVersions.Compare("banana", "1.0.0"));
        Assert.Throws<ArgumentException>(() => PackageVersions.Compare("1.0.0", "banana"));
    }

    [Fact]
    public void Sort_PutsTheNewestFirstAndDropsDuplicates()
    {
        ImmutableArray<string> sorted = PackageVersions.Sort(
            ["1.0.0", "2.0.0-rc.1", "1.10.0", "1.2.0", "2.0.0", "1.0.0"]);

        Assert.Equal(["2.0.0", "2.0.0-rc.1", "1.10.0", "1.2.0", "1.0.0"], sorted);
    }

    [Fact]
    public void Sort_CanPutTheOldestFirst() =>
        Assert.Equal(["1.0.0", "2.0.0"], PackageVersions.Sort(["2.0.0", "1.0.0"], newestFirst: false));

    /// <summary>
    /// A feed that offers a version NuGet cannot parse has offered something no project could
    /// reference. Refusing the whole list because of it would make one bad entry in a package's
    /// history hide every good one.
    /// </summary>
    [Fact]
    public void Sort_LeavesOutWhatItCannotRead() =>
        Assert.Equal(["2.0.0", "1.0.0"], PackageVersions.Sort(["1.0.0", "banana", null, "", "2.0.0"]));

    /// <summary>The version a project asked for is the version it gets back, spelt its way.</summary>
    [Fact]
    public void Sort_KeepsTheOriginalSpelling() =>
        Assert.Equal(["1.0"], PackageVersions.Sort(["1.0"]));

    [Fact]
    public void Latest_IgnoresPrereleasesByDefault() =>
        Assert.Equal("1.2.0", PackageVersions.Latest(["1.0.0", "1.2.0", "2.0.0-rc.1"]));

    [Fact]
    public void Latest_CanBeAskedForAPrerelease() =>
        Assert.Equal(
            "2.0.0-rc.1",
            PackageVersions.Latest(["1.0.0", "1.2.0", "2.0.0-rc.1"], includePrerelease: true));

    /// <summary>
    /// Quietly handing back a prerelease to somebody who asked for "the latest version" is how a
    /// tool installs an alpha into a production project.
    /// </summary>
    [Fact]
    public void Latest_OfAPackageWithOnlyPrereleases_IsNothing() =>
        Assert.Null(PackageVersions.Latest(["1.0.0-alpha", "1.0.0-beta"]));

    [Fact]
    public void Latest_OfNothing_IsNothing()
    {
        Assert.Null(PackageVersions.Latest([]));
        Assert.Null(PackageVersions.Latest(["banana", null]));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PackageVersions.Sort(null!));
        Assert.Throws<ArgumentNullException>(() => PackageVersions.Latest(null!));
    }
}

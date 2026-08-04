using System;
using System.IO;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

public sealed class CanonicalPathTests
{
    [Fact]
    public void Default_IsNone()
    {
        CanonicalPath path = default;

        Assert.True(path.IsEmpty);
        Assert.Equal(CanonicalPath.None, path);
        Assert.Equal(string.Empty, path.Value);
    }

    [Fact]
    public void Create_WithMixedSeparators_ProducesOnePath()
    {
        CanonicalPath slashes = CanonicalPath.Create(TestPaths.Slashes("src", "App", "App.csproj"));
        CanonicalPath backslashes = CanonicalPath.Create(TestPaths.Backslashes("src", "App", "App.csproj"));

        Assert.Equal(slashes, backslashes);
        Assert.Equal(slashes.GetHashCode(), backslashes.GetHashCode());
    }

    [Fact]
    public void Create_WithDifferentCase_ProducesEqualPaths()
    {
        CanonicalPath lower = CanonicalPath.Create(TestPaths.Native("src", "app", "app.csproj"));
        CanonicalPath upper = CanonicalPath.Create(TestPaths.Native("SRC", "APP", "APP.csproj"));

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.Equal(0, lower.CompareTo(upper));
    }

    [Fact]
    public void Create_PreservesTheCasingItWasGiven()
    {
        CanonicalPath path = CanonicalPath.Create(TestPaths.Native("Src", "App", "App.csproj"));

        Assert.Contains("App.csproj", path.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithTrailingSeparator_MatchesTheSamePathWithout()
    {
        CanonicalPath bare = CanonicalPath.Create(TestPaths.Native("src", "App"));
        CanonicalPath trailing = CanonicalPath.Create(TestPaths.Native("src", "App") + Path.DirectorySeparatorChar);

        Assert.Equal(bare, trailing);
    }

    [Fact]
    public void Create_AtTheRoot_KeepsItsSeparator()
    {
        CanonicalPath root = CanonicalPath.Create(TestPaths.Root);

        Assert.False(root.IsEmpty);
        Assert.Equal(TestPaths.Root, root.Value);
    }

    [Fact]
    public void Create_ResolvesDotAndDotDot()
    {
        CanonicalPath direct = CanonicalPath.Create(TestPaths.Native("src", "App", "App.csproj"));
        CanonicalPath indirect = CanonicalPath.Create(
            TestPaths.Native("src", "Other", "..", "App", ".", "App.csproj"));

        Assert.Equal(direct, indirect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path.csproj")]
    [InlineData("also\\relative.csproj")]
    public void Create_WithSomethingThatIsNotAnAbsolutePath_Throws(string? value)
    {
        Assert.Throws<ArgumentException>(() => CanonicalPath.Create(value!));
    }

    [Fact]
    public void Create_WithANullCharacter_Throws()
    {
        Assert.Throws<ArgumentException>(() => CanonicalPath.Create(TestPaths.Native("src\0App")));
    }

    /// <summary>
    /// The one shape that would otherwise resolve against a per-drive current directory, which is
    /// exactly the dependency a canonical path must not have.
    /// </summary>
    [Fact]
    public void Create_WithADriveRelativePath_Throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<ArgumentException>(() => CanonicalPath.Create("C:App.csproj"));
    }

    [Fact]
    public void TryCreate_WithBadInput_ReturnsFalseAndNone()
    {
        Assert.False(CanonicalPath.TryCreate("nonsense", out CanonicalPath path));
        Assert.Equal(CanonicalPath.None, path);
    }

    [Fact]
    public void TryCreate_WithGoodInput_ReturnsTheCanonicalForm()
    {
        Assert.True(CanonicalPath.TryCreate(TestPaths.Native("src", "App.csproj"), out CanonicalPath path));
        Assert.Equal(CanonicalPath.Create(TestPaths.Native("src", "App.csproj")), path);
    }

    [Fact]
    public void Create_FromABaseDirectory_ResolvesRelativeSegments()
    {
        CanonicalPath directory = CanonicalPath.Create(TestPaths.Native("src", "App"));

        Assert.Equal(
            CanonicalPath.Create(TestPaths.Native("src", "Core", "Core.csproj")),
            CanonicalPath.Create(directory, Path.Combine("..", "Core", "Core.csproj")));
    }

    [Fact]
    public void Create_FromABaseDirectory_AcceptsAnAbsolutePathAndIgnoresTheBase()
    {
        CanonicalPath directory = CanonicalPath.Create(TestPaths.Native("src", "App"));
        CanonicalPath absolute = CanonicalPath.Create(TestPaths.Native("elsewhere", "Other.csproj"));

        Assert.Equal(absolute, CanonicalPath.Create(directory, absolute.Value));
    }

    [Fact]
    public void Create_FromAnAbsentBaseDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => CanonicalPath.Create(CanonicalPath.None, "App.csproj"));
    }

    [Fact]
    public void FileNameAndExtensionAndDirectory_DecomposeThePath()
    {
        CanonicalPath path = CanonicalPath.Create(TestPaths.Native("src", "App", "App.csproj"));

        Assert.Equal("App.csproj", path.FileName);
        Assert.Equal(".csproj", path.Extension);
        Assert.Equal(CanonicalPath.Create(TestPaths.Native("src", "App")), path.Directory);
    }

    [Fact]
    public void Directory_AtTheRoot_IsNone()
    {
        Assert.True(CanonicalPath.Create(TestPaths.Root).Directory.IsEmpty);
    }

    [Fact]
    public void Decomposition_OfNone_IsEmptyRatherThanAThrow()
    {
        CanonicalPath none = CanonicalPath.None;

        Assert.Equal(string.Empty, none.FileName);
        Assert.Equal(string.Empty, none.Extension);
        Assert.True(none.Directory.IsEmpty);
    }

    /// <summary>
    /// A plain string prefix test would say <c>App</c> contains <c>AppOther</c>, which is the bug
    /// this method exists to not have.
    /// </summary>
    [Fact]
    public void StartsWith_RespectsSegmentBoundaries()
    {
        CanonicalPath directory = CanonicalPath.Create(TestPaths.Native("src", "App"));

        Assert.True(CanonicalPath.Create(TestPaths.Native("src", "App", "Program.cs")).StartsWith(directory));
        Assert.True(directory.StartsWith(directory));
        Assert.False(CanonicalPath.Create(TestPaths.Native("src", "AppOther", "Program.cs")).StartsWith(directory));
    }

    [Fact]
    public void StartsWith_AtTheRoot_IsTrueForEverythingUnderIt()
    {
        Assert.True(CanonicalPath.Create(TestPaths.Native("src", "App.csproj"))
            .StartsWith(CanonicalPath.Create(TestPaths.Root)));
    }

    [Fact]
    public void StartsWith_InvolvingNone_IsFalse()
    {
        CanonicalPath path = TestPaths.Project();

        Assert.False(path.StartsWith(CanonicalPath.None));
        Assert.False(CanonicalPath.None.StartsWith(path));
    }

    [Fact]
    public void Combine_AppendsARelativeSegment()
    {
        CanonicalPath directory = CanonicalPath.Create(TestPaths.Native("src", "App"));

        Assert.Equal(
            CanonicalPath.Create(TestPaths.Native("src", "App", "Program.cs")),
            directory.Combine("Program.cs"));
    }

    [Fact]
    public void Ordering_AgreesWithEquality()
    {
        CanonicalPath a = CanonicalPath.Create(TestPaths.Native("a.csproj"));
        CanonicalPath b = CanonicalPath.Create(TestPaths.Native("b.csproj"));

        Assert.True(a < b);
        Assert.True(a <= b);
        Assert.True(b > a);
        Assert.True(b >= a);
        Assert.True(a <= CanonicalPath.Create(TestPaths.Native("A.csproj")));
        Assert.True(a >= CanonicalPath.Create(TestPaths.Native("A.csproj")));
    }

    [Fact]
    public void Equality_Operators_MatchEquals()
    {
        CanonicalPath a = TestPaths.Project();
        CanonicalPath same = TestPaths.Project();
        CanonicalPath other = TestPaths.Project("Core");

        Assert.True(a == same);
        Assert.False(a != same);
        Assert.True(a != other);
        Assert.False(a.Equals((object)"not a path"));
        Assert.True(a.Equals((object)same));
    }

    [Fact]
    public void ToString_ReturnsTheCanonicalText()
    {
        CanonicalPath path = TestPaths.Project();

        Assert.Equal(path.Value, path.ToString());
    }
}

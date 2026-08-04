using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

public sealed class ProjectMetadataTests
{
    [Fact]
    public void Empty_HasNothingInIt()
    {
        Assert.Empty(ProjectMetadata.Empty);
        Assert.False(ProjectMetadata.Empty.ContainsKey("anything"));
    }

    [Fact]
    public void Create_WithNoEntries_ReturnsEmpty()
    {
        Assert.Same(ProjectMetadata.Empty, ProjectMetadata.Create([]));
    }

    /// <summary>
    /// MSBuild property and metadata names are case-insensitive, so a map that treated them
    /// otherwise would report one property as two.
    /// </summary>
    [Fact]
    public void Keys_CompareCaseInsensitively()
    {
        ProjectMetadata metadata = ProjectMetadata.Create(
            [new KeyValuePair<string, string>("TargetFramework", "net10.0")]);

        Assert.True(metadata.ContainsKey("targetframework"));
        Assert.True(metadata.ContainsKey("TARGETFRAMEWORK"));
        Assert.Equal("net10.0", metadata["TaRgEtFrAmEwOrK"]);
        Assert.True(metadata.TryGetValue("targetframework", out string? value));
        Assert.Equal("net10.0", value);
    }

    [Fact]
    public void Values_CompareExactly()
    {
        ProjectMetadata lower = ProjectMetadata.Create([new KeyValuePair<string, string>("K", "value")]);
        ProjectMetadata upper = ProjectMetadata.Create([new KeyValuePair<string, string>("K", "VALUE")]);

        Assert.NotEqual(lower, upper);
    }

    [Fact]
    public void Create_WithADuplicateKey_KeepsTheLastValue()
    {
        ProjectMetadata metadata = ProjectMetadata.Create(
        [
            new KeyValuePair<string, string>("Key", "first"),
            new KeyValuePair<string, string>("KEY", "second"),
        ]);

        Assert.Single(metadata);
        Assert.Equal("second", metadata["key"]);
    }

    [Fact]
    public void Create_CopiesTheSourceCollection()
    {
        var source = new List<KeyValuePair<string, string>>
        {
            new("Key", "value"),
        };

        ProjectMetadata metadata = ProjectMetadata.Create(source);
        source.Add(new KeyValuePair<string, string>("Added", "later"));

        Assert.Single(metadata);
        Assert.False(metadata.ContainsKey("Added"));
    }

    [Fact]
    public void Equality_IsStructuralAndOrderIndependent()
    {
        ProjectMetadata first = ProjectMetadata.Create(
        [
            new KeyValuePair<string, string>("A", "1"),
            new KeyValuePair<string, string>("B", "2"),
        ]);

        ProjectMetadata second = ProjectMetadata.Create(
        [
            new KeyValuePair<string, string>("b", "2"),
            new KeyValuePair<string, string>("a", "1"),
        ]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first == second);
        Assert.False(first != second);
    }

    [Fact]
    public void Equality_NoticesADifferentCount()
    {
        ProjectMetadata one = ProjectMetadata.Create([new KeyValuePair<string, string>("A", "1")]);
        ProjectMetadata two = ProjectMetadata.Create(
        [
            new KeyValuePair<string, string>("A", "1"),
            new KeyValuePair<string, string>("B", "2"),
        ]);

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void Equality_HandlesNull()
    {
        ProjectMetadata metadata = ProjectMetadata.Create([new KeyValuePair<string, string>("A", "1")]);

        Assert.False(metadata.Equals(null));
        Assert.False(metadata == null);
        Assert.True(metadata != null);
        Assert.True((ProjectMetadata?)null == (ProjectMetadata?)null);
    }

    [Fact]
    public void GetValueOrDefault_ReturnsNullForAnAbsentKey()
    {
        ProjectMetadata metadata = ProjectMetadata.Create([new KeyValuePair<string, string>("A", "1")]);

        Assert.Equal("1", metadata.GetValueOrDefault("a"));
        Assert.Null(metadata.GetValueOrDefault("missing"));
    }

    [Fact]
    public void Indexer_ThrowsForAnAbsentKey()
    {
        Assert.Throws<KeyNotFoundException>(() => ProjectMetadata.Empty["missing"]);
    }

    [Fact]
    public void Create_WithABlankKey_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ProjectMetadata.Create([new KeyValuePair<string, string>("  ", "value")]));
    }

    [Fact]
    public void Create_WithANullValue_StoresAnEmptyString()
    {
        ProjectMetadata metadata = ProjectMetadata.Create([new KeyValuePair<string, string>("K", null!)]);

        Assert.Equal(string.Empty, metadata["K"]);
    }

    [Fact]
    public void Enumeration_YieldsEveryEntry()
    {
        ProjectMetadata metadata = ProjectMetadata.Create(
        [
            new KeyValuePair<string, string>("A", "1"),
            new KeyValuePair<string, string>("B", "2"),
        ]);

        Assert.Equal(2, metadata.Count);
        Assert.Equal(["A", "B"], metadata.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["1", "2"], metadata.Values.Order(StringComparer.Ordinal));
    }
}

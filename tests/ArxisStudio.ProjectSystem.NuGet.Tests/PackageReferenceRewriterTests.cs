using System;
using System.Xml.Linq;
using Xunit;

namespace ArxisStudio.ProjectSystem.NuGet.Tests;

/// <summary>
/// Editing the XML of a project file.
/// </summary>
/// <remarks>
/// Pure over an <see cref="XDocument"/>, so every case here is exact and none of them touches a
/// file. Most of the assertions are about what did <em>not</em> change: a project file is something
/// a person wrote and keeps reading, and an edit that reformats it produces a correct file and an
/// unreviewable diff.
/// </remarks>
public sealed class PackageReferenceRewriterTests
{
    private const string Reference = "PackageReference";

    [Fact]
    public void AddingToAProjectWithNoItems_MakesAGroupForIt()
    {
        XDocument document = Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        Assert.Equal(RewriteOutcome.Changed, Add(document, "Serilog", "4.1.0"));

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"), Render(document));
    }

    [Fact]
    public void AddingToAProjectThatAlreadyHasPackages_JoinsThem()
    {
        XDocument document = Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>
            """);

        Add(document, "Xunit", "3.2.2");

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
                <PackageReference Include="Xunit" Version="3.2.2" />
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"), Render(document));
    }

    /// <summary>
    /// A file that is already alphabetical is one somebody is keeping alphabetical, so appending
    /// would undo work rather than avoid it.
    /// </summary>
    [Fact]
    public void AddingToASortedList_KeepsItSorted()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Alpha" Version="1.0.0" />
                <PackageReference Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Add(document, "Mike", "1.0.0");

        Assert.Equal("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Alpha" Version="1.0.0" />
                <PackageReference Include="Mike" Version="1.0.0" />
                <PackageReference Include="Zulu" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"), Render(document));
    }

    /// <summary>
    /// And a file that is not sorted was grouped by something else — by feature, by layer, by
    /// whatever the team meant. Sorting it as a side effect of adding one line rearranges their file
    /// for them.
    /// </summary>
    [Fact]
    public void AddingToAnUnsortedList_AppendsWithoutRearranging()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Zulu" Version="1.0.0" />
                <PackageReference Include="Alpha" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Add(document, "Mike", "1.0.0");

        Assert.Equal("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Zulu" Version="1.0.0" />
                <PackageReference Include="Alpha" Version="1.0.0" />
                <PackageReference Include="Mike" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"), Render(document));
    }

    [Fact]
    public void AddingSomethingAlreadyThere_DoesNothing()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>
            """);

        string before = Render(document);

        Assert.Equal(RewriteOutcome.NothingToDo, Add(document, "Serilog", "9.9.9"));
        Assert.Equal(before, Render(document));
    }

    /// <summary>NuGet compares identifiers case-insensitively, so these are one package, not two.</summary>
    [Fact]
    public void AddingSomethingSpeltDifferently_IsStillAlreadyThere()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(RewriteOutcome.NothingToDo, Add(document, "SERILOG", "9.9.9"));
    }

    [Fact]
    public void AddingWithExtraMetadata_WritesItInOrder()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>
            """);

        PackageReferenceRewriter.Add(
            document, Reference, "StyleCop.Analyzers",
            ("Version", "1.2.0"),
            ("PrivateAssets", "all"),
            ("IncludeAssets", null));

        Assert.Contains(
            """<PackageReference Include="StyleCop.Analyzers" Version="1.2.0" PrivateAssets="all" />""",
            Render(document),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An element in the wrong namespace is invisible to MSBuild while looking perfectly correct in
    /// the file, which is the worst combination available.
    /// </summary>
    [Fact]
    public void AddingToAnOldStyleProject_UsesItsNamespace()
    {
        XDocument document = Parse("""
            <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>
            """);

        Add(document, "Xunit", "3.2.2");

        string rendered = Render(document);

        Assert.Contains("""<PackageReference Include="Xunit" Version="3.2.2" />""", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("xmlns=\"\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Setting_ChangesOnlyThatAttribute()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <!-- logging -->
                <PackageReference Include="Serilog" Version="4.1.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(RewriteOutcome.Changed, PackageReferenceRewriter.Set(document, Reference, "Serilog", "Version", "5.0.0"));

        Assert.Equal("""
            <Project>
              <ItemGroup>
                <!-- logging -->
                <PackageReference Include="Serilog" Version="5.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"), Render(document));
    }

    [Fact]
    public void SettingWhatIsAlreadySet_DoesNothing() =>
        Assert.Equal(
            RewriteOutcome.NothingToDo,
            PackageReferenceRewriter.Set(WithSerilog(), Reference, "Serilog", "Version", "4.1.0"));

    [Fact]
    public void SettingSomethingThatIsNotThere_DoesNothing() =>
        Assert.Equal(
            RewriteOutcome.NothingToDo,
            PackageReferenceRewriter.Set(WithSerilog(), Reference, "Absent", "Version", "1.0.0"));

    /// <summary>
    /// How a project adjusts a reference it did not declare — a transitively pinned version, or one
    /// an SDK contributed.
    /// </summary>
    [Fact]
    public void AnItemDeclaredWithUpdate_IsFoundToo()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Update="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(PackageReferenceRewriter.Contains(document, Reference, "Serilog"));
        Assert.Equal("4.1.0", PackageReferenceRewriter.Read(document, Reference, "Serilog", "Version"));
    }

    [Fact]
    public void Removing_TakesTheLineAndNotTheLayout()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
                <PackageReference Include="Xunit" Version="3.2.2" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(RewriteOutcome.Changed, PackageReferenceRewriter.Remove(document, Reference, "Serilog"));

        Assert.Equal("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Xunit" Version="3.2.2" />
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"), Render(document));
    }

    /// <summary>A group that exists to hold nothing is noise the next reader has to skip past.</summary>
    [Fact]
    public void RemovingTheLastItem_TakesTheEmptyGroupWithIt()
    {
        XDocument document = Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.1.0" />
              </ItemGroup>
            </Project>
            """);

        PackageReferenceRewriter.Remove(document, Reference, "Serilog");

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """.ReplaceLineEndings("\n"), Render(document));
    }

    /// <summary>
    /// A group left empty but commented was deliberate, and the comment explains items that may come
    /// back. Taking it away would delete somebody's note.
    /// </summary>
    [Fact]
    public void RemovingTheLastItemFromACommentedGroup_KeepsTheGroup()
    {
        XDocument document = Parse("""
            <Project>
              <ItemGroup>
                <!-- Analyzers only; nothing here ships. -->
                <PackageReference Include="StyleCop.Analyzers" Version="1.2.0" />
              </ItemGroup>
            </Project>
            """);

        PackageReferenceRewriter.Remove(document, Reference, "StyleCop.Analyzers");

        Assert.Equal("""
            <Project>
              <ItemGroup>
                <!-- Analyzers only; nothing here ships. -->
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"), Render(document));
    }

    [Fact]
    public void RemovingSomethingThatIsNotThere_DoesNothing() =>
        Assert.Equal(
            RewriteOutcome.NothingToDo,
            PackageReferenceRewriter.Remove(WithSerilog(), Reference, "Absent"));

    [Fact]
    public void AProjectWithNoRootAtAll_IsNotAnException()
    {
        var document = new XDocument();

        Assert.False(PackageReferenceRewriter.Contains(document, Reference, "Serilog"));
        Assert.Equal(RewriteOutcome.NothingToDo, PackageReferenceRewriter.Remove(document, Reference, "Serilog"));
    }

    private static RewriteOutcome Add(XDocument document, string id, string version) =>
        PackageReferenceRewriter.Add(document, Reference, id, ("Version", version));

    private static XDocument WithSerilog() => Parse("""
        <Project>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="4.1.0" />
          </ItemGroup>
        </Project>
        """);

    /// <summary>
    /// Parses with the same options the editor uses, so layout survives — and with line endings
    /// pinned, so these assertions say the same thing whatever the checkout did to the file they
    /// are written in.
    /// </summary>
    private static XDocument Parse(string xml) =>
        XDocument.Parse(xml.ReplaceLineEndings("\n"), LoadOptions.PreserveWhitespace);

    /// <summary>Renders the way the editor writes, so an assertion sees what a file would hold.</summary>
    private static string Render(XDocument document)
    {
        var text = new System.IO.StringWriter();

        var settings = new System.Xml.XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = System.Xml.NewLineHandling.None,
        };

        using (System.Xml.XmlWriter writer = System.Xml.XmlWriter.Create(text, settings))
        {
            document.Save(writer);
        }

        return text.ToString();
    }
}

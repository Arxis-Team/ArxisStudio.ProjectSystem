using System;
using System.Linq;
using Xunit;
using static ArxisStudio.ProjectSystem.MSBuild.Tests.TestEvaluation;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// The mapping from an evaluation to a snapshot, which is where this package's bugs will be.
/// </summary>
/// <remarks>
/// Not one of these runs MSBuild. The evaluation shape is built by hand, so each case states
/// exactly the input it is about and nothing else varies — which is what makes a failure point at
/// the rule that broke rather than at the environment.
/// </remarks>
public sealed class MSBuildProjectTranslatorTests
{
    [Fact]
    public void Identity_IsDerivedFromTheWorkspaceAndThePath()
    {
        ProjectSnapshot snapshot = Translate(Project());

        Assert.Equal(ProjectIdentity.Create(Workspace, ProjectPath()), snapshot.Identity);
        Assert.Equal(ProjectPath(), snapshot.ProjectFilePath);
        Assert.Equal(ProjectPath().Directory, snapshot.ProjectDirectory);
        Assert.Equal("MSBuild", snapshot.ProviderName);
    }

    [Fact]
    public void Name_PrefersMSBuildProjectName()
    {
        Assert.Equal("Chosen", Translate(Project(properties: Meta(
            ("MSBuildProjectName", "Chosen"), ("AssemblyName", "Ignored")))).Name);
    }

    [Fact]
    public void Name_FallsBackToAssemblyNameAndThenToTheFileName()
    {
        Assert.Equal("FromAssembly", Translate(Project(properties: Meta(("AssemblyName", "FromAssembly")))).Name);
        Assert.Equal("App", Translate(Project()).Name);
    }

    [Theory]
    [InlineData(".csproj", "C#")]
    [InlineData(".fsproj", "F#")]
    [InlineData(".vbproj", "Visual Basic")]
    [InlineData(".vcxproj", "C++")]
    public void Language_ComesFromTheExtension(string extension, string expected)
    {
        Assert.Equal(expected, Translate(Project(path: ProjectPath("App", extension))).Language);
    }

    [Fact]
    public void Language_OfAnUnknownExtension_IsAbsent()
    {
        Assert.Null(Translate(Project(path: ProjectPath("App", ".weirdproj"))).Language);
    }

    [Fact]
    public void Kind_ComesFromOutputType()
    {
        Assert.Equal("Library", Translate(Project(properties: Meta(("OutputType", "Library")))).Kind);
        Assert.Null(Translate(Project()).Kind);
    }

    /// <summary>
    /// A cross-targeting project sets both, and the singular names whichever framework the current
    /// evaluation is for. Reading it first would report a multi-targeting project as targeting one
    /// thing.
    /// </summary>
    [Fact]
    public void TargetFrameworks_PrefersThePluralProperty()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(
            ("TargetFrameworks", "net10.0;net8.0"), ("TargetFramework", "net8.0"))));

        Assert.Equal(["net10.0", "net8.0"], snapshot.TargetFrameworks);
        Assert.Equal("net8.0", snapshot.ActiveTargetFramework);
    }

    [Fact]
    public void TargetFrameworks_FallsBackToTheSingular()
    {
        Assert.Equal(["net10.0"], Translate(Project(properties: Meta(("TargetFramework", "net10.0")))).TargetFrameworks);
    }

    [Fact]
    public void TargetFrameworks_OfAProjectThatSaysNothing_AreEmptyNotDefault()
    {
        ProjectSnapshot snapshot = Translate(Project());

        Assert.False(snapshot.TargetFrameworks.IsDefault);
        Assert.Empty(snapshot.TargetFrameworks);
        Assert.Null(snapshot.ActiveTargetFramework);
    }

    [Fact]
    public void SemicolonLists_AreTrimmedAndDeduplicated()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(
            ("Configurations", " Debug ; Release ;; Debug "), ("Platforms", "AnyCPU;x64"))));

        Assert.Equal(["Debug", "Release"], snapshot.Configurations);
        Assert.Equal(["AnyCPU", "x64"], snapshot.Platforms);
    }

    [Fact]
    public void ActiveConfigurationAndPlatform_ComeFromTheSingularProperties()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(
            ("Configuration", "Release"), ("Platform", "x64"))));

        Assert.Equal("Release", snapshot.ActiveConfiguration);
        Assert.Equal("x64", snapshot.ActivePlatform);
    }

    /// <summary>
    /// A full evaluation produces about a thousand properties, most of them bookkeeping. Carrying
    /// all of them on every project would cost megabytes to answer a question nobody asked.
    /// </summary>
    [Fact]
    public void Properties_AreCuratedRatherThanCopiedWholesale()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(
            ("TargetPath", "bin/App.dll"),
            ("_SomeInternalMSBuildBookkeeping", "noise"),
            ("MSBuildToolsVersion", "also noise"))));

        Assert.True(snapshot.Properties.ContainsKey("TargetPath"));
        Assert.False(snapshot.Properties.ContainsKey("_SomeInternalMSBuildBookkeeping"));
        Assert.False(snapshot.Properties.ContainsKey("MSBuildToolsVersion"));
    }

    [Fact]
    public void Properties_KeepMSBuildsCaseInsensitiveComparison()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(("outputtype", "Exe"))));

        Assert.Equal("Exe", snapshot.Properties["OutputType"]);
        Assert.Equal("Exe", snapshot.Kind);
    }

    [Fact]
    public void AProjectReference_BecomesATypedReference()
    {
        CanonicalPath core = CanonicalPath.Create(Native("src", "Core", "Core.csproj"));

        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "ProjectReference", "../Core/Core.csproj", core,
            Meta(("Aliases", "global,core"), ("ReferenceOutputAssembly", "false")))));

        ProjectReferenceInfo reference = Assert.Single(snapshot.ProjectReferences);

        Assert.Equal(core, reference.ProjectFilePath);
        Assert.Equal(["global", "core"], reference.Aliases);
        Assert.False(reference.ReferenceOutputAssembly);
        Assert.Empty(snapshot.Items);
    }

    /// <summary>
    /// Opening one project says nothing about which other projects a workspace holds, so minting an
    /// identity for the target here would mint one for a project nobody loaded.
    /// </summary>
    [Fact]
    public void AProjectReference_HasNoIdentityUntilTheGraphIsKnown()
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "ProjectReference", "../Core/Core.csproj",
            CanonicalPath.Create(Native("src", "Core", "Core.csproj")))));

        Assert.True(Assert.Single(snapshot.ProjectReferences).Project.IsEmpty);
    }

    [Fact]
    public void AProjectReference_WithoutAFullPath_IsResolvedAgainstTheProjectDirectory()
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item("ProjectReference", "../Core/Core.csproj")));

        Assert.Equal(
            CanonicalPath.Create(Native("src", "Core", "Core.csproj")),
            Assert.Single(snapshot.ProjectReferences).ProjectFilePath);
    }

    /// <summary>The one place the translator must not be clever: NuGet ranges are NuGet's business.</summary>
    [Theory]
    [InlineData("4.1.0")]
    [InlineData("[1.0,2.0)")]
    [InlineData("1.2.*")]
    [InlineData("$(SerilogVersion)")]
    public void APackageReference_CarriesItsVersionAsWrittenText(string version)
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "PackageReference", "Serilog", metadata: Meta(("Version", version)))));

        PackageReferenceInfo reference = Assert.Single(snapshot.PackageReferences);

        Assert.Equal("Serilog", reference.PackageId);
        Assert.Equal(version, reference.VersionText);
    }

    [Fact]
    public void APackageReference_CarriesItsAssetFlagsUninterpreted()
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "PackageReference", "StyleCop", metadata: Meta(
                ("PrivateAssets", "all"), ("IncludeAssets", "runtime;build"), ("ExcludeAssets", "none")))));

        PackageReferenceInfo reference = Assert.Single(snapshot.PackageReferences);

        Assert.Equal("all", reference.PrivateAssets);
        Assert.Equal("runtime;build", reference.IncludeAssets);
        Assert.Equal("none", reference.ExcludeAssets);
    }

    [Fact]
    public void APackageReference_WithNoVersion_HasNoneRatherThanABlank()
    {
        Assert.Null(Assert.Single(
            Translate(Project(items: Item("PackageReference", "Serilog"))).PackageReferences).VersionText);
    }

    /// <summary>
    /// The distinction anything that edits a project file needs, because it rewrites one file.
    /// </summary>
    /// <remarks>
    /// An evaluated project lists more package references than its own file declares: a
    /// <c>Directory.Build.props</c> or a <c>GlobalPackageReference</c> contributes them too. Asking
    /// the editor to change the version of one of those sends it looking in a file that does not
    /// mention it.
    /// </remarks>
    [Theory]
    [InlineData(false, ProjectItemOrigin.Declared)]
    [InlineData(true, ProjectItemOrigin.Imported)]
    public void APackageReference_SaysWhetherTheProjectFileItselfDeclaredIt(
        bool imported,
        ProjectItemOrigin expected)
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "PackageReference", "Serilog", imported: imported)));

        Assert.Equal(expected, Assert.Single(snapshot.PackageReferences).Origin);
    }

    [Fact]
    public void AFrameworkReference_BecomesATypedReference()
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item("FrameworkReference", "Microsoft.AspNetCore.App")));

        Assert.Equal("Microsoft.AspNetCore.App", Assert.Single(snapshot.FrameworkReferences).Name);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void AnAssemblyReference_ResolvesItsHintPath()
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "Reference", "Legacy", metadata: Meta(("HintPath", "../lib/Legacy.dll"), ("Private", "true")))));

        AssemblyReferenceInfo reference = Assert.Single(snapshot.AssemblyReferences);

        Assert.Equal("Legacy", reference.Name);
        Assert.Equal(CanonicalPath.Create(Native("src", "lib", "Legacy.dll")), reference.HintPath);
        Assert.True(reference.Private);
    }

    [Fact]
    public void AnAssemblyReference_WithoutAHintPath_HasNone()
    {
        AssemblyReferenceInfo reference = Assert.Single(
            Translate(Project(items: Item("Reference", "System.Xml"))).AssemblyReferences);

        Assert.True(reference.HintPath.IsEmpty);
        Assert.True(reference.ResolvedPath.IsEmpty);
        Assert.Null(reference.Private);
    }

    [Fact]
    public void AnAnalyzer_BecomesATypedReference()
    {
        CanonicalPath analyzer = CanonicalPath.Create(Native("packages", "StyleCop.dll"));

        ProjectSnapshot snapshot = Translate(Project(items: Item("Analyzer", "StyleCop.dll", analyzer)));

        Assert.Equal(analyzer, Assert.Single(snapshot.AnalyzerReferences).AssemblyPath);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void AnythingElse_BecomesAPlainItem()
    {
        CanonicalPath source = CanonicalPath.Create(Native("src", "App", "Program.cs"));

        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "Compile", "Program.cs", source, Meta(("Link", "Sources/Program.cs")))));

        ProjectItem item = Assert.Single(snapshot.Items);

        Assert.Equal("Compile", item.ItemType);
        Assert.Equal("Program.cs", item.Include);
        Assert.Equal(source, item.FullPath);
        Assert.Equal("Sources/Program.cs", item.Link);
        Assert.Equal(ProjectItemOrigin.Declared, item.Origin);
    }

    [Fact]
    public void AnImportedItem_SaysSo()
    {
        Assert.Equal(
            ProjectItemOrigin.Imported,
            Assert.Single(Translate(Project(items: Item("Compile", "Generated.cs", imported: true))).Items).Origin);
    }

    [Fact]
    public void ItemTypes_AreMatchedCaseInsensitivelyAsMSBuildDoes()
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item("packagereference", "Serilog")));

        Assert.Single(snapshot.PackageReferences);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void Outputs_ComeFromTheEvaluatedTargetPath()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(
            ("TargetPath", Native("src", "App", "bin", "App.dll")),
            ("DocumentationFile", "bin/App.xml"),
            ("TargetFramework", "net10.0"))));

        Assert.Equal(2, snapshot.Outputs.Length);

        OutputArtifact assembly = snapshot.Outputs.Single(static o => o.Kind == OutputArtifactKind.Assembly);

        Assert.Equal(CanonicalPath.Create(Native("src", "App", "bin", "App.dll")), assembly.Path);
        Assert.Equal("net10.0", assembly.TargetFramework);

        Assert.Contains(snapshot.Outputs, static o => o.Kind == OutputArtifactKind.DocumentationFile);
    }

    /// <summary>
    /// Everything a consumer needs to assemble a runtime environment, from one evaluation. The
    /// properties are the ones a real SDK project carries; which of them gates which artifact was
    /// measured against the SDK rather than assumed.
    /// </summary>
    [Fact]
    public void Outputs_DescribeEverythingTheBuildWillProduce()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(
            ("TargetPath", Native("src", "App", "bin", "App.dll")),
            ("DebugType", "portable"),
            ("TargetRefPath", Native("src", "App", "obj", "ref", "App.dll")),
            ("GenerateDependencyFile", "true"),
            ("ProjectDepsFilePath", Native("src", "App", "bin", "App.deps.json")),
            ("GenerateRuntimeConfigurationFiles", "true"),
            ("ProjectRuntimeConfigFilePath", Native("src", "App", "bin", "App.runtimeconfig.json")),
            ("DocumentationFile", Native("src", "App", "bin", "App.xml")),
            ("TargetFramework", "net10.0"))));

        Assert.Equal(6, snapshot.Outputs.Length);
        Assert.All(snapshot.Outputs, static o => Assert.Equal("net10.0", o.TargetFramework));

        Assert.Equal(
            CanonicalPath.Create(Native("src", "App", "obj", "ref", "App.dll")),
            Single(snapshot, OutputArtifactKind.ReferenceAssembly).Path);

        Assert.Equal(
            CanonicalPath.Create(Native("src", "App", "bin", "App.deps.json")),
            Single(snapshot, OutputArtifactKind.DependencyManifest).Path);

        Assert.Equal(
            CanonicalPath.Create(Native("src", "App", "bin", "App.runtimeconfig.json")),
            Single(snapshot, OutputArtifactKind.RuntimeConfiguration).Path);
    }

    /// <summary>
    /// Symbols are the one artifact no property names, so the path is composed the way the SDK
    /// composes it — beside the assembly, with the extension changed.
    /// </summary>
    [Fact]
    public void Symbols_SitBesideTheAssembly()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(
            ("TargetPath", Native("src", "App", "bin", "App.dll")),
            ("DebugType", "portable"))));

        Assert.Equal(
            CanonicalPath.Create(Native("src", "App", "bin", "App.pdb")),
            Single(snapshot, OutputArtifactKind.SymbolFile).Path);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    public void AProjectThatEmitsNoSymbols_ReportsNone(string debugType)
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(
            ("TargetPath", Native("src", "App", "bin", "App.dll")),
            ("DebugType", debugType))));

        Assert.DoesNotContain(snapshot.Outputs, static o => o.Kind == OutputArtifactKind.SymbolFile);
    }

    /// <summary>
    /// The case that makes the gates necessary, and it is not hypothetical: an ordinary library
    /// carries a populated <c>ProjectRuntimeConfigFilePath</c> and emits no such file. Reporting the
    /// path because it has a value would name a file that never appears.
    /// </summary>
    [Theory]
    [InlineData("GenerateRuntimeConfigurationFiles", "ProjectRuntimeConfigFilePath", OutputArtifactKind.RuntimeConfiguration)]
    [InlineData("GenerateDependencyFile", "ProjectDepsFilePath", OutputArtifactKind.DependencyManifest)]
    public void AnArtifactTheBuildWillNotEmit_IsNotReported(string gate, string path, OutputArtifactKind kind)
    {
        ProjectSnapshot withoutTheGate = Translate(Project(properties: Meta(
            (path, Native("src", "App", "bin", "App.json")))));

        Assert.DoesNotContain(withoutTheGate.Outputs, o => o.Kind == kind);

        ProjectSnapshot toldItIsOff = Translate(Project(properties: Meta(
            (gate, "false"),
            (path, Native("src", "App", "bin", "App.json")))));

        Assert.DoesNotContain(toldItIsOff.Outputs, o => o.Kind == kind);
    }

    private static OutputArtifact Single(ProjectSnapshot snapshot, OutputArtifactKind kind) =>
        Assert.Single(snapshot.Outputs.Where(o => o.Kind == kind));

    /// <summary>
    /// The project file is always an input, because changing it is the most obvious way to make a
    /// snapshot stale and a project with no imports at all is still watchable.
    /// </summary>
    [Fact]
    public void EvaluationInputs_AlwaysIncludeTheProjectFile()
    {
        ProjectSnapshot snapshot = Translate(Project());

        Assert.Equal([snapshot.ProjectFilePath], snapshot.EvaluationInputs);
    }

    /// <summary>
    /// The filter this exists for. Everything under the .NET installation or the package cache is
    /// build logic nobody edits, and a real project imports well over a hundred of them.
    /// </summary>
    [Fact]
    public void EvaluationInputs_DropWhatBelongsToTheToolchain()
    {
        ProjectSnapshot snapshot = Translate(Project(
            properties: Meta(
                ("NetCoreRoot", Native("dotnet") + System.IO.Path.DirectorySeparatorChar),
                ("NuGetPackageRoot", Native("packages") + System.IO.Path.DirectorySeparatorChar)),
            imports:
            [
                CanonicalPath.Create(Native("dotnet", "sdk", "10.0.301", "Sdk.props")),
                CanonicalPath.Create(Native("dotnet", "sdk-manifests", "10.0.100", "WorkloadManifest.targets")),
                CanonicalPath.Create(Native("packages", "serilog", "4.1.0", "build", "Serilog.props")),
                CanonicalPath.Create(Native("src", "Directory.Build.props")),
            ]));

        Assert.Equal(
            [
                CanonicalPath.Create(Native("src", "App", "App.csproj")),
                CanonicalPath.Create(Native("src", "Directory.Build.props")),
            ],
            snapshot.EvaluationInputs);
    }

    /// <summary>
    /// A sibling of the SDK directory, not a child of it. Filtering on the SDK folder would leak
    /// every workload manifest, which is why the rule uses the installation root.
    /// </summary>
    [Fact]
    public void EvaluationInputs_DropWorkloadManifestsBesideTheSdk()
    {
        ProjectSnapshot snapshot = Translate(Project(
            properties: Meta(("NetCoreRoot", Native("dotnet"))),
            imports: [CanonicalPath.Create(Native("dotnet", "sdk-manifests", "Workload.targets"))]));

        Assert.DoesNotContain(
            snapshot.EvaluationInputs,
            p => p.Value.Contains("sdk-manifests", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Restore writes these, they change when packages change, and a change to one means the project
    /// imports something different than it did. So they stay even though nobody types in them.
    /// </summary>
    [Fact]
    public void EvaluationInputs_KeepTheGeneratedRestoreImports()
    {
        CanonicalPath generated = CanonicalPath.Create(Native("src", "App", "obj", "App.csproj.nuget.g.props"));

        ProjectSnapshot snapshot = Translate(Project(
            properties: Meta(("NetCoreRoot", Native("dotnet"))),
            imports: [generated]));

        Assert.Contains(generated, snapshot.EvaluationInputs);
    }

    [Fact]
    public void EvaluationInputs_IncludeTheAssetsFileWhenThereIsOne()
    {
        CanonicalPath assets = CanonicalPath.Create(Native("src", "App", "obj", "project.assets.json"));

        ProjectSnapshot snapshot = Translate(Project(properties: Meta(("ProjectAssetsFile", assets.Value))));

        Assert.Contains(assets, snapshot.EvaluationInputs);
    }

    /// <summary>
    /// The assets file is normally imported as well as named by a property, and a change to it must
    /// cost one comparison rather than two for the rest of the session.
    /// </summary>
    [Fact]
    public void EvaluationInputs_ListEachFileOnce()
    {
        CanonicalPath shared = CanonicalPath.Create(Native("src", "Directory.Build.props"));

        ProjectSnapshot snapshot = Translate(Project(
            properties: Meta(("ProjectAssetsFile", shared.Value)),
            imports: [shared, shared]));

        Assert.Single(snapshot.EvaluationInputs.Where(p => p == shared));
    }

    [Fact]
    public void Outputs_OfAProjectThatSaysNothing_AreEmpty()
    {
        ProjectSnapshot snapshot = Translate(Project());

        Assert.False(snapshot.Outputs.IsDefault);
        Assert.Empty(snapshot.Outputs);
    }

    /// <summary>
    /// A property that is not a usable path — a leftover token, a value some target built from
    /// pieces — is not a broken project, and inventing a path for it would be worse than having
    /// none.
    /// </summary>
    [Fact]
    public void APropertyThatIsNotAPath_ProducesNoArtifactAndNoThrow()
    {
        ProjectSnapshot snapshot = Translate(Project(properties: Meta(("TargetPath", "   "))));

        Assert.Empty(snapshot.Outputs);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", null)]
    [InlineData("yes", null)]
    public void AnMSBuildBoolean_ThatIsNotRecognised_MeansTheProjectDidNotSay(string value, bool? expected)
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "ProjectReference", "../Core/Core.csproj",
            CanonicalPath.Create(Native("src", "Core", "Core.csproj")),
            Meta(("ReferenceOutputAssembly", value)))));

        Assert.Equal(expected, Assert.Single(snapshot.ProjectReferences).ReferenceOutputAssembly);
    }

    [Theory]
    [InlineData("a,b")]
    [InlineData("a;b")]
    [InlineData(" a , b ")]
    public void Aliases_SplitOnEitherSeparator(string value)
    {
        ProjectSnapshot snapshot = Translate(Project(items: Item(
            "Reference", "Legacy", metadata: Meta(("Aliases", value)))));

        Assert.Equal(["a", "b"], Assert.Single(snapshot.AssemblyReferences).Aliases);
    }

    [Fact]
    public void EveryCollection_IsEmptyRatherThanDefault()
    {
        ProjectSnapshot snapshot = Translate(Project());

        Assert.False(snapshot.Items.IsDefault);
        Assert.False(snapshot.ProjectReferences.IsDefault);
        Assert.False(snapshot.PackageReferences.IsDefault);
        Assert.False(snapshot.FrameworkReferences.IsDefault);
        Assert.False(snapshot.AssemblyReferences.IsDefault);
        Assert.False(snapshot.AnalyzerReferences.IsDefault);
        Assert.False(snapshot.Outputs.IsDefault);
        Assert.False(snapshot.Diagnostics.IsDefault);
        Assert.Empty(snapshot.Properties);
    }
}

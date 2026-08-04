namespace ArxisStudio.ProjectSystem;

/// <summary>What a build produced.</summary>
public enum OutputArtifactKind
{
    /// <summary>The provider did not say.</summary>
    Unknown = 0,

    /// <summary>The primary output assembly.</summary>
    Assembly = 1,

    /// <summary>Debug symbols.</summary>
    SymbolFile = 2,

    /// <summary>The <c>.deps.json</c> dependency manifest.</summary>
    DependencyManifest = 3,

    /// <summary>The <c>.runtimeconfig.json</c> host configuration.</summary>
    RuntimeConfiguration = 4,

    /// <summary>The generated XML documentation file.</summary>
    DocumentationFile = 5,

    /// <summary>The reference assembly, which carries the public surface and no bodies.</summary>
    ReferenceAssembly = 6,

    /// <summary>A file produced by the build, such as source-generator output.</summary>
    GeneratedFile = 7,

    /// <summary>A directory of produced files.</summary>
    GeneratedDirectory = 8,
}

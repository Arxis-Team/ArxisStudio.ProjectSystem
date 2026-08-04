namespace ArxisStudio.ProjectSystem;

/// <summary>
/// The item types a consumer is most likely to look for, spelled once.
/// </summary>
/// <remarks>
/// Not an exhaustive list, and deliberately not an enum: MSBuild item types are open — a project,
/// an SDK, or a custom target may define any name at all. These constants exist so that the common
/// cases are not spelled by hand at every call site, not to suggest the set is closed.
/// Comparison is case-insensitive, as MSBuild's own is.
/// </remarks>
public static class ProjectItemTypes
{
    /// <summary>Source files passed to the compiler.</summary>
    public const string Compile = "Compile";

    /// <summary>Files copied or published alongside the output.</summary>
    public const string Content = "Content";

    /// <summary>Files that participate in no build step by default.</summary>
    public const string None = "None";

    /// <summary>Files embedded into the produced assembly.</summary>
    public const string EmbeddedResource = "EmbeddedResource";

    /// <summary>Files offered to analyzers and source generators.</summary>
    public const string AdditionalFiles = "AdditionalFiles";

    /// <summary>Analyzer assemblies.</summary>
    public const string Analyzer = "Analyzer";

    /// <summary>A folder carried for the sake of the tree, with no file of its own.</summary>
    public const string Folder = "Folder";
}

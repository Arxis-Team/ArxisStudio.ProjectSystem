namespace ArxisStudio.ProjectSystem;

/// <summary>The neutral kind of thing a workspace was asked to open.</summary>
public enum WorkspaceEntryPointKind
{
    /// <summary>Nothing here recognises the extension. A provider may still accept it.</summary>
    Unknown = 0,

    /// <summary>A classic <c>.sln</c> solution file.</summary>
    Solution = 1,

    /// <summary>An XML <c>.slnx</c> solution file.</summary>
    SolutionXml = 2,

    /// <summary>A project file, by the MSBuild convention that project extensions end in <c>proj</c>.</summary>
    Project = 3,
}

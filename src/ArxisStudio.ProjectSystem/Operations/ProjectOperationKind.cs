namespace ArxisStudio.ProjectSystem;

/// <summary>What an operation asks a provider to do.</summary>
/// <remarks>
/// Every one of these changes state on disk, which is what separates them from everything else in
/// this library. None of them happens on its own: an operation runs because a caller asked for it.
/// </remarks>
public enum ProjectOperationKind
{
    /// <summary>Resolve packages and write the restore output the model reads.</summary>
    Restore = 0,

    /// <summary>Compile what is out of date.</summary>
    Build = 1,

    /// <summary>Compile everything, whether or not it is out of date.</summary>
    Rebuild = 2,

    /// <summary>Delete build output.</summary>
    Clean = 3,
}

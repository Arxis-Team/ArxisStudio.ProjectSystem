namespace ArxisStudio.ProjectSystem;

/// <summary>Where a project item came from, when a provider can tell.</summary>
/// <remarks>
/// A genuinely closed set, unlike an item's type — which is why this is an enum and
/// <see cref="ProjectItem.ItemType"/> is a string.
/// </remarks>
public enum ProjectItemOrigin
{
    /// <summary>The provider did not say.</summary>
    Unknown = 0,

    /// <summary>Written in the project file itself.</summary>
    Declared = 1,

    /// <summary>Contributed by an import, an SDK, or a default glob.</summary>
    Imported = 2,

    /// <summary>Produced by the build rather than authored.</summary>
    Generated = 3,
}

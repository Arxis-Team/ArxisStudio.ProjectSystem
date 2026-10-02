namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Codes for what designing against a project's own types runs into.
/// </summary>
/// <remarks>
/// <c>APS5xxx</c> is the adapters' range. Every code here has a producer; a code nothing raises
/// would be a promise this library has not made.
/// </remarks>
public static class ProjectDesignDiagnosticCodes
{
    /// <summary>
    /// <c>APS5001</c> — two assemblies of a design set share a simple name, and only the first is
    /// loaded.
    /// </summary>
    /// <remarks>
    /// A generation answers a name with one assembly, as the runtime does. Two projects building an
    /// assembly of one name, or two versions of one package across the set, cannot both be there;
    /// the first in the set's order is, and a type only the other has is not found.
    /// </remarks>
    public const string AssemblyNameConflict = "APS5001";

    /// <summary>
    /// <c>APS5002</c> — a project of the design set builds an assembly the host process already has,
    /// and the host's copy answers for it.
    /// </summary>
    /// <remarks>
    /// One copy of a name per process is what keeps one <c>Button</c> type in it, so the process's
    /// own wins — and the project's build of that name is not what the designer shows. Only a
    /// separate process could show it.
    /// </remarks>
    public const string ShadowedByHost = "APS5002";
}

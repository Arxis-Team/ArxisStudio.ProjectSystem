namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>
/// The stable diagnostic codes this package raises.
/// </summary>
/// <remarks>
/// <para>
/// <c>APS4xxx</c> is the range reserved for package-management operations. Reading what a project
/// already declares is the MSBuild provider's <c>APS2xxx</c>; these are raised only by changing it.
/// </para>
/// <para>
/// Only codes something actually raises are declared. A code with no producer is a promise the
/// library has not made, and a consumer who wrote a rule for it would be waiting for an event that
/// never arrives.
/// </para>
/// </remarks>
public static class PackageDiagnosticCodes
{
    /// <summary>
    /// <c>APS4001</c> — the project file is not there.
    /// </summary>
    /// <remarks>
    /// Kept distinct from the MSBuild provider's own missing-file code because nothing was going to
    /// be evaluated: a stale entry in a recent-projects list is a different situation from a
    /// solution that lists a project somebody deleted.
    /// </remarks>
    public const string ProjectFileNotFound = "APS4001";

    /// <summary>
    /// <c>APS4002</c> — a file that had to be edited could not be read or written.
    /// </summary>
    /// <remarks>
    /// Locked by another process, read-only, or on a volume that went away. Ordinary rather than
    /// exceptional: a project open in another editor is a situation a tool handles, not a bug.
    /// </remarks>
    public const string FileNotWritable = "APS4002";

    /// <summary>
    /// <c>APS4003</c> — a file that had to be edited is not usable XML.
    /// </summary>
    /// <remarks>
    /// Reported rather than repaired. A project file somebody has broken by hand is theirs to fix,
    /// and rewriting it from a partial parse would lose whatever else is in it.
    /// </remarks>
    public const string FileNotWellFormed = "APS4003";

    /// <summary>
    /// <c>APS4004</c> — the package is already there, or is not there to begin with.
    /// </summary>
    /// <remarks>
    /// Installing something a project already declares, or removing something it does not, is a
    /// no-op rather than a failure of the edit — but a silent no-op looks exactly like a successful
    /// change, so it is said out loud.
    /// </remarks>
    public const string NothingToChange = "APS4004";

    /// <summary>
    /// <c>APS4005</c> — central package management is on, and the file holding the versions is not
    /// where the project says it is.
    /// </summary>
    /// <remarks>
    /// Under central management a version cannot be written into the project, so there is nowhere
    /// for it to go. Distinct from <see cref="ProjectFileNotFound"/> because the project is fine and
    /// the repository's configuration is not.
    /// </remarks>
    public const string CentralVersionFileNotFound = "APS4005";
}

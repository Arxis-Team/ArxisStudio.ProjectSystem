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

    /// <summary>
    /// <c>APS4006</c> — a package feed could not be reached.
    /// </summary>
    /// <remarks>
    /// The ordinary state of a tool on an aeroplane, behind a proxy, or pointed at a private source
    /// that wants credentials. A search that fails this way found nothing <em>and says so</em>,
    /// which is a different answer from a search that matched nothing.
    /// </remarks>
    public const string FeedUnreachable = "APS4006";

    /// <summary>
    /// <c>APS4007</c> — a feed answered, and the answer was not one this library understands.
    /// </summary>
    /// <remarks>
    /// A service index advertising no search service, or a response that is not the documented
    /// shape. Separate from <see cref="FeedUnreachable"/> because the network is fine and the
    /// remedy is different: this one is about which feed was configured.
    /// </remarks>
    public const string FeedNotUnderstood = "APS4007";

    /// <summary>
    /// <c>APS4008</c> — the restore after a change failed, so the change was undone.
    /// </summary>
    /// <remarks>
    /// A warning rather than an error: undoing was the right thing and it worked. The error beside
    /// it is the restore's own, which says what actually went wrong — a version that does not exist
    /// reads very differently from a feed that could not be reached.
    /// </remarks>
    public const string ChangeUndone = "APS4008";

    /// <summary>
    /// <c>APS4009</c> — the restore failed and the change could not be undone either.
    /// </summary>
    /// <remarks>
    /// The bad case, and the reason it has a code of its own: the project is left carrying a
    /// reference that does not restore, and somebody has to be told rather than left to discover it
    /// at the next build.
    /// </remarks>
    public const string ChangeNotUndone = "APS4009";
}

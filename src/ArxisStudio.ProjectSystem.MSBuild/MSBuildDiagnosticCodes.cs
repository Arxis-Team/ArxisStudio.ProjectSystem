namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// The stable diagnostic codes this package raises.
/// </summary>
/// <remarks>
/// <para>
/// <c>APS2xxx</c> is the range reserved for MSBuild discovery and evaluation. The core owns
/// <c>APS1xxx</c>; the ranges above this one belong to packages that do not exist yet.
/// </para>
/// <para>
/// Only codes something actually raises are declared. A code with no producer is a promise the
/// library has not made, and a consumer who wrote a rule for it would be waiting for an event that
/// never arrives.
/// </para>
/// </remarks>
public static class MSBuildDiagnosticCodes
{
    /// <summary>
    /// <c>APS2001</c> — no MSBuild could be located, so nothing can be evaluated.
    /// </summary>
    /// <remarks>
    /// An environment problem rather than a project problem: the .NET SDK is missing, or the path a
    /// host registered does not hold one. It is still a diagnostic rather than an exception, because
    /// a consumer opening a project should be told what is wrong rather than handed a stack trace.
    /// </remarks>
    public const string MSBuildNotFound = "APS2001";

    /// <summary>
    /// <c>APS2002</c> — the project file could not be evaluated.
    /// </summary>
    /// <remarks>
    /// The ordinary case: malformed XML, a missing import, an SDK the machine does not have, a
    /// property function that threw. MSBuild's own message is carried through, because it is
    /// usually the useful part.
    /// </remarks>
    public const string EvaluationFailed = "APS2002";

    /// <summary>
    /// <c>APS2004</c> — the solution file could not be read.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="EvaluationFailed"/> because nothing was evaluated: the solution
    /// itself is malformed or is a format this provider does not read, so there is no project to
    /// attribute the failure to.
    /// </remarks>
    public const string SolutionReadFailed = "APS2004";

    /// <summary>
    /// <c>APS2003</c> — the project file is not there.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="EvaluationFailed"/> because it is the one a tool most wants to
    /// handle on its own — a stale entry in a recent-projects list is not a broken project.
    /// </remarks>
    public const string ProjectFileNotFound = "APS2003";
}

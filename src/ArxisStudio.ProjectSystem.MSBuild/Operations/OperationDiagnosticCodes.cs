namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// The stable diagnostic codes raised by running targets, as opposed to evaluating projects.
/// </summary>
/// <remarks>
/// <para>
/// <c>APS3xxx</c> is the range reserved for restore and build execution. It is a separate range from
/// the <c>APS2xxx</c> of <see cref="MSBuildDiagnosticCodes"/> even though the same package raises
/// both, because the ranges are divided by concern rather than by assembly: a consumer routing on
/// "the build failed" should not have to know which package happened to run it.
/// </para>
/// <para>
/// Conditions that mean the same thing in both keep their <c>APS2xxx</c> code. A project file that
/// is not there is <see cref="MSBuildDiagnosticCodes.ProjectFileNotFound"/> whether it was going to
/// be evaluated or built — inventing a second code for one condition would make a consumer handle it
/// twice.
/// </para>
/// <para>
/// Only codes something actually raises are declared. Most of what a failed build reports arrives
/// under the engine's own codes rather than these; see
/// <see href="../../docs/adr/0013-a-provider-may-keep-its-engines-diagnostic-codes.md">ADR 0013</see>.
/// </para>
/// </remarks>
public static class OperationDiagnosticCodes
{
    /// <summary>
    /// <c>APS3001</c> — an operation failed and the engine did not say why.
    /// </summary>
    /// <remarks>
    /// A build that fails logs an error, and one that logs an error fails, so this should never be
    /// raised. It exists because an unexplained failure tells a consumer nothing, and if the engine
    /// ever disagrees with itself a synthetic explanation is better than none.
    /// </remarks>
    public const string OperationFailed = "APS3001";
}

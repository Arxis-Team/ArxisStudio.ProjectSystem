namespace ArxisStudio.ProjectSystem;

/// <summary>
/// The stable diagnostic codes this package raises, and the ranges reserved for the packages
/// that will build on it.
/// </summary>
/// <remarks>
/// <para>
/// Reserved ranges:
/// </para>
/// <list type="table">
///   <listheader><term>Range</term><description>Raised by</description></listheader>
///   <item><term><c>APS1xxx</c></term><description>Core: requests, paths, snapshots, workspace, invariants</description></item>
///   <item><term><c>APS2xxx</c></term><description>MSBuild discovery and evaluation</description></item>
///   <item><term><c>APS3xxx</c></term><description>Restore and build execution</description></item>
///   <item><term><c>APS4xxx</c></term><description>NuGet package-management operations</description></item>
///   <item><term><c>APS5xxx</c></term><description>Integration adapters</description></item>
/// </list>
/// <para>
/// Only codes something actually raises are declared. A code with no producer is a promise the
/// library has not made, and a consumer that wrote a rule for it would be waiting for an event
/// that never arrives.
/// </para>
/// <para>
/// A code, once published, keeps its meaning. Retiring one is a breaking change for every consumer
/// that suppresses or routes on it.
/// </para>
/// </remarks>
public static class ProjectDiagnosticCodes
{
    /// <summary>
    /// <c>APS1001</c> — no configured provider accepted the entry point.
    /// </summary>
    /// <remarks>
    /// This subsumes "unsupported file extension" without the core having to hard-code which
    /// extensions exist. Whether a provider can open something is the provider's judgement, and a
    /// core that pre-judged it would have to be edited every time a new project type appeared.
    /// </remarks>
    public const string UnsupportedEntryPoint = "APS1001";

    /// <summary>
    /// <c>APS1002</c> — a provider threw instead of returning a result.
    /// </summary>
    /// <remarks>
    /// The exception's type and message go into the diagnostic's message; the exception itself
    /// does not reach the caller, because a provider's failure mode is not part of this library's
    /// contract. Cancellation is excluded: that surfaces as
    /// <see cref="System.OperationCanceledException"/>, never as a diagnostic.
    /// </remarks>
    public const string ProviderFailed = "APS1002";

    /// <summary>
    /// <c>APS1004</c> — no configured provider performs the operation that was asked for.
    /// </summary>
    /// <remarks>
    /// Building is an optional capability: a provider that reads projects need not be able to build
    /// them, and one that invents projects in memory cannot. Asking for a build where nothing can
    /// build is an ordinary situation a host has to handle, not a broken configuration.
    /// </remarks>
    public const string UnsupportedOperation = "APS1004";

    /// <summary>
    /// <c>APS1003</c> — a provider returned a result that breaks the boundary's contract.
    /// </summary>
    /// <remarks>
    /// Raised for a snapshot stamped with a different workspace's identity, an entry point that
    /// disagrees with the request, or duplicate project identities. These are the shapes that
    /// would silently corrupt identity determinism if they were published, so they are refused at
    /// the boundary rather than trusted.
    /// </remarks>
    public const string InvalidProviderResult = "APS1003";
}

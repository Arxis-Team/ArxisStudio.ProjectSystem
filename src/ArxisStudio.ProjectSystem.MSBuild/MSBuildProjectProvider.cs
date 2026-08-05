using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Opens a project file by evaluating it with MSBuild.
/// </summary>
/// <remarks>
/// <para>
/// This milestone opens a <b>standalone project</b>. Solutions, and the project graph that comes
/// with them, are the next one — <see cref="CanLoad"/> says so by accepting only
/// <see cref="WorkspaceEntryPointKind.Project"/>, which lets a workspace configured with several
/// providers pass a solution to whichever one grows that ability without this one having to
/// pretend.
/// </para>
/// <para>
/// <b>Evaluation happens in this process</b>, and see
/// <c>docs/adr/0009-evaluation-happens-in-process.md</c> for what that costs and what it would take
/// to change. Nothing a consumer can see depends on the answer, which was the point of building the
/// model the way ADR 0003 describes.
/// </para>
/// <para>
/// <b>Evaluation is not interruptible.</b> MSBuild offers no way to abandon one part-way, so the
/// token is observed before the work starts and after it finishes, and a cancellation during a slow
/// evaluation takes effect when that evaluation ends. The work runs on the thread pool rather than
/// on the caller's thread, so cancelling still returns control immediately to whoever asked.
/// </para>
/// </remarks>
public sealed class MSBuildProjectProvider : IProjectSystemProvider
{
    /// <inheritdoc />
    public string Name => "MSBuild";

    /// <inheritdoc />
    public bool CanLoad(WorkspaceEntryPoint entryPoint) =>
        entryPoint.Kind == WorkspaceEntryPointKind.Project;

    /// <inheritdoc />
    public async ValueTask<WorkspaceLoadResult> LoadAsync(
        WorkspaceLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        // Nothing in this method may name a Microsoft.Build type: the locator has to run before one
        // is loaded, and the runtime loads on first entry to a method that names one. Everything
        // that does live in MSBuildProjectEvaluator, which nothing enters before this line.
        if (!TryRegister(out string? registrationError))
        {
            return Failure(MSBuildDiagnosticCodes.MSBuildNotFound, registrationError!, request.EntryPointPath);
        }

        if (!File.Exists(request.EntryPointPath.Value))
        {
            return Failure(
                MSBuildDiagnosticCodes.ProjectFileNotFound,
                $"There is no project file at '{request.EntryPointPath}'.",
                request.EntryPointPath);
        }

        Dictionary<string, string> globalProperties = GlobalProperties(request);
        bool includeItems = request.Options.IncludeItems;
        CanonicalPath path = request.EntryPointPath;

        (EvaluatedProject? evaluated, string? evaluationError) = await Task.Run(
            () => MSBuildProjectEvaluator.TryEvaluate(path, globalProperties, includeItems, out EvaluatedProject? result, out string? error)
                ? (result, (string?)null)
                : (null, error),
            cancellationToken).ConfigureAwait(false);

        // A slow evaluation cannot be abandoned part-way, so this is where a cancellation that
        // arrived during it takes effect. Publishing the result anyway would advance a version the
        // caller asked not to advance.
        cancellationToken.ThrowIfCancellationRequested();

        if (evaluated is null)
        {
            return Failure(
                MSBuildDiagnosticCodes.EvaluationFailed,
                $"'{request.EntryPointPath}' could not be evaluated: {evaluationError}",
                request.EntryPointPath);
        }

        ProjectSnapshot project = MSBuildProjectTranslator.Translate(evaluated, request.Workspace, Name);

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = request.Workspace,

            // A standalone project has no solution, and that absence is the honest answer rather
            // than a solution invented to hold one project.
            Solution = SolutionIdentity.None,
            Name = project.Name,
            ProviderName = Name,
            Request = request,
        };

        solution.Projects.Add(project);

        return WorkspaceLoadResult.Success(solution.ToSnapshot());
    }

    /// <summary>
    /// Registers MSBuild, turning the environment failure into something reportable.
    /// </summary>
    /// <remarks>
    /// A missing SDK is not a broken project, but it is also not an exception a consumer opening a
    /// project should have to catch. It becomes <c>APS2001</c> instead.
    /// </remarks>
    private static bool TryRegister(out string? error)
    {
        try
        {
            MSBuildEnvironment.Register();

            error = null;

            return true;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;

            return false;
        }
    }

    private static Dictionary<string, string> GlobalProperties(WorkspaceLoadRequest request)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> property in request.GlobalProperties)
        {
            properties[property.Key] = property.Value;
        }

        // The request's own choices win over anything it also passed as a global property, because
        // they are the more specific statement of the same thing.
        Set(properties, "Configuration", request.Configuration);
        Set(properties, "Platform", request.Platform);
        Set(properties, "TargetFramework", request.TargetFramework);

        return properties;
    }

    private static void Set(Dictionary<string, string> properties, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[name] = value;
        }
    }

    private static WorkspaceLoadResult Failure(string code, string message, CanonicalPath path) =>
        WorkspaceLoadResult.Failure(
            ProjectDiagnostic.ForFile(code, message, ProjectDiagnosticSeverity.Error, path) with
            {
                ProviderName = "MSBuild",
            });
}

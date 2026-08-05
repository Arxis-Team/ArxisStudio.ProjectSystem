using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// The MSBuild provider's ability to change what is on disk.
/// </summary>
/// <remarks>
/// Kept in its own file because it is a separate capability with a separate boundary: reading a
/// project and building one are different things, and a reader of either half should not have to
/// scroll past the other.
/// </remarks>
public sealed partial class MSBuildProjectProvider : IProjectOperationProvider
{
    /// <inheritdoc />
    public bool CanExecute(ProjectOperationKind kind) =>
        kind is ProjectOperationKind.Restore
            or ProjectOperationKind.Build
            or ProjectOperationKind.Rebuild
            or ProjectOperationKind.Clean;

    /// <inheritdoc />
    public async ValueTask<ProjectOperationResult> ExecuteAsync(
        ProjectOperationRequest request,
        IProgress<ProjectOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        // Same ordering rule as loading: nothing in this method may name a Microsoft.Build type.
        // MSBuildOperationRunner is where those names live, and nothing enters it before this.
        if (!TryRegister(out string? registrationError))
        {
            return ProjectOperationResult.Failed(Diagnostic(
                MSBuildDiagnosticCodes.MSBuildNotFound, registrationError!, request.EntryPointPath));
        }

        if (!File.Exists(request.EntryPointPath.Value))
        {
            return ProjectOperationResult.Failed(Diagnostic(
                MSBuildDiagnosticCodes.ProjectFileNotFound,
                $"There is nothing at '{request.EntryPointPath}'.",
                request.EntryPointPath));
        }

        string[] targets = Targets(request.Kind);
        Dictionary<string, string> properties = Properties(request);
        CanonicalPath entryPoint = request.EntryPointPath;

        OperationOutcome outcome = await Task.Run(
            () => MSBuildOperationRunner.Run(
                entryPoint,
                targets,
                properties,
                progress is null
                    ? null
                    : message => progress.Report(new ProjectOperationProgress { Message = message }),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        List<ProjectDiagnostic> diagnostics = [.. Translate(outcome, entryPoint)];

        if (outcome.Succeeded)
        {
            return ProjectOperationResult.Succeeded(diagnostics);
        }

        // A build that fails logs an error, and one that logs an error fails -- but if the engine
        // ever disagrees with itself, an unexplained failure is worse than a synthetic explanation.
        if (!diagnostics.Exists(static d => d.IsError))
        {
            diagnostics.Add(Diagnostic(
                OperationDiagnosticCodes.OperationFailed,
                $"{request.Kind} of '{entryPoint.FileName}' failed without reporting a reason.",
                entryPoint));
        }

        return ProjectOperationResult.Failed(diagnostics);
    }

    /// <summary>
    /// The targets each kind runs.
    /// </summary>
    /// <remarks>
    /// <c>Rebuild</c> is one target rather than <c>Clean;Build</c>, because MSBuild defines it and
    /// projects are free to override what it means for them.
    /// </remarks>
    private static string[] Targets(ProjectOperationKind kind) => kind switch
    {
        ProjectOperationKind.Restore => ["Restore"],
        ProjectOperationKind.Build => ["Build"],
        ProjectOperationKind.Rebuild => ["Rebuild"],
        ProjectOperationKind.Clean => ["Clean"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown operation kind."),
    };

    private static Dictionary<string, string> Properties(ProjectOperationRequest request)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> property in request.GlobalProperties)
        {
            properties[property.Key] = property.Value;
        }

        Set(properties, "Configuration", request.Configuration);
        Set(properties, "Platform", request.Platform);
        Set(properties, "TargetFramework", request.TargetFramework);

        if (request.Kind == ProjectOperationKind.Restore)
        {
            // What MSBuild's own /restore switch sets. Restore has to evaluate without the imports
            // that restore itself produces, or a first restore in a clean tree would need its own
            // output to exist before it could create it.
            properties["MSBuildRestoreSessionId"] = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            properties["ExcludeRestorePackageImports"] = "true";
        }

        return properties;
    }

    private static IEnumerable<ProjectDiagnostic> Translate(OperationOutcome outcome, CanonicalPath entryPoint)
    {
        foreach (EngineMessage message in outcome.Messages)
        {
            // The engine's own codes, kept. See ADR 0013: a compiler error is CS0103 to everyone who
            // has met one, and renaming it would leave a caller parsing text to tell it apart.
            yield return new ProjectDiagnostic(
                message.Code,
                message.Message,
                message.IsError ? ProjectDiagnosticSeverity.Error : ProjectDiagnosticSeverity.Warning)
            {
                FilePath = message.File.IsEmpty ? entryPoint : message.File,
                Span = message.Line > 0 ? FileSpan.At(message.Line, message.Column) : FileSpan.None,
                ProviderName = "MSBuild",
            };
        }
    }
}

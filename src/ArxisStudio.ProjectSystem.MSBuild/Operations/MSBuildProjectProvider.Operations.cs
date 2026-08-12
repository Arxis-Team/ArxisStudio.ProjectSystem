using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        string[] targets = Targets(request.Kind);
        Dictionary<string, string> properties = Properties(request);

        var diagnostics = new List<ProjectDiagnostic>();
        var succeeded = true;

        // The whole entry point, or the named projects and nothing else. Passing a project's own
        // file to the engine is what "just this one" means to MSBuild, and it is what makes a
        // designer's rebuild-on-save proportionate to the save rather than to the solution.
        foreach ((CanonicalPath file, ProjectIdentity project) in ProjectFiles(request))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(file.Value))
            {
                diagnostics.Add(Diagnostic(
                    MSBuildDiagnosticCodes.ProjectFileNotFound,
                    $"There is nothing at '{file}'.",
                    file));

                succeeded = false;

                continue;
            }

            OperationOutcome outcome = await Task.Run(
                () => MSBuildOperationRunner.Run(
                    file,
                    targets,
                    properties,
                    progress is null
                        ? null
                        : message => progress.Report(
                            new ProjectOperationProgress { Message = message, Project = project }),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            int before = diagnostics.Count;

            diagnostics.AddRange(Translate(outcome, file));

            if (outcome.Succeeded)
            {
                continue;
            }

            succeeded = false;

            // A build that fails logs an error, and one that logs an error fails -- but if the
            // engine ever disagrees with itself, an unexplained failure is worse than a synthetic
            // explanation.
            if (!diagnostics.Skip(before).Any(static d => d.IsError))
            {
                diagnostics.Add(Diagnostic(
                    OperationDiagnosticCodes.OperationFailed,
                    $"{request.Kind} of '{file.FileName}' failed without reporting a reason.",
                    file));
            }
        }

        return succeeded
            ? ProjectOperationResult.Succeeded(diagnostics)
            : ProjectOperationResult.Failed(diagnostics);
    }

    /// <summary>
    /// What the request asks to operate on: each named project's own file, or the entry point when
    /// it names none.
    /// </summary>
    /// <remarks>
    /// A foreign or half-formed identity is a programming error rather than a tool scenario — an
    /// identity carries the workspace that minted it, so one from elsewhere cannot have come from
    /// this request's snapshot — and is refused as an argument rather than reported as a
    /// diagnostic.
    /// </remarks>
    private static List<(CanonicalPath File, ProjectIdentity Project)> ProjectFiles(
        ProjectOperationRequest request)
    {
        if (request.Projects.IsEmpty)
        {
            return [(request.EntryPointPath, ProjectIdentity.None)];
        }

        var files = new List<(CanonicalPath File, ProjectIdentity Project)>(request.Projects.Length);
        var seen = new HashSet<ProjectIdentity>();

        foreach (ProjectIdentity project in request.Projects)
        {
            if (project.IsEmpty)
            {
                throw new ArgumentException(
                    "The request names an empty project identity.", nameof(request));
            }

            if (project.Workspace != request.Workspace)
            {
                throw new ArgumentException(
                    $"The request names {project}, which belongs to another workspace.", nameof(request));
            }

            if (seen.Add(project))
            {
                files.Add((project.ProjectFilePath, project));
            }
        }

        return files;
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
        Dictionary<string, string> properties = GlobalProperties(
            request.GlobalProperties, request.Configuration, request.Platform, request.TargetFramework);

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
            yield return Translate(message, entryPoint);
        }
    }
}

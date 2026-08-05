using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Opens a solution or a standalone project by evaluating it with MSBuild.
/// </summary>
/// <remarks>
/// <para>
/// <b>Evaluation happens in this process</b>, and see
/// <c>docs/adr/0009-evaluation-happens-in-process.md</c> for what that costs and what it would take
/// to change. Nothing a consumer can see depends on the answer, which was the point of building the
/// model the way ADR 0003 describes.
/// </para>
/// <para>
/// <b>Evaluation is not interruptible.</b> MSBuild offers no way to abandon one part-way, so the
/// token is observed between projects and after the work finishes, and a cancellation during a slow
/// evaluation takes effect when that evaluation ends. The work runs on the thread pool rather than
/// on the caller's thread, so cancelling still returns control immediately to whoever asked.
/// </para>
/// <para>
/// <b>A solution's projects are evaluated one at a time.</b> Correctness first: each gets its own
/// <c>ProjectCollection</c>, and nothing here yet says that evaluating several at once is safe with
/// the SDK resolvers and caches MSBuild installs per process. Recorded in
/// <c>docs/limitations.md</c> as the obvious thing to measure when a large solution is slow.
/// </para>
/// </remarks>
public sealed class MSBuildProjectProvider : IProjectSystemProvider
{
    /// <inheritdoc />
    public string Name => "MSBuild";

    /// <inheritdoc />
    public bool CanLoad(WorkspaceEntryPoint entryPoint) =>
        entryPoint.Kind is WorkspaceEntryPointKind.Project
            or WorkspaceEntryPointKind.Solution
            or WorkspaceEntryPointKind.SolutionXml;

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
                $"There is nothing at '{request.EntryPointPath}'.",
                request.EntryPointPath);
        }

        return request.EntryPoint.Kind == WorkspaceEntryPointKind.Project
            ? await LoadProjectAsync(request, cancellationToken).ConfigureAwait(false)
            : await LoadSolutionAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<WorkspaceLoadResult> LoadProjectAsync(
        WorkspaceLoadRequest request,
        CancellationToken cancellationToken)
    {
        (ProjectSnapshot? project, ProjectDiagnostic? failure) = await EvaluateAsync(
            request.EntryPointPath,
            request.EntryPointPath.FileName,
            request,
            knownProjects: null,
            cancellationToken).ConfigureAwait(false);

        if (project is null)
        {
            return WorkspaceLoadResult.Failure(failure!);
        }

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

    private async ValueTask<WorkspaceLoadResult> LoadSolutionAsync(
        WorkspaceLoadRequest request,
        CancellationToken cancellationToken)
    {
        (DiscoveredSolution? discovered, string? error) =
            await MSBuildSolutionReader.ReadAsync(request.EntryPointPath, cancellationToken).ConfigureAwait(false);

        if (discovered is null)
        {
            return Failure(
                MSBuildDiagnosticCodes.SolutionReadFailed,
                $"'{request.EntryPointPath}' could not be read: {error}",
                request.EntryPointPath);
        }

        // Every project the solution lists, known before any of them is evaluated. That is what
        // lets a project reference be resolved to an identity on the first pass rather than needing
        // a second one to stitch the graph together.
        HashSet<CanonicalPath> knownProjects = [.. Enumerate(discovered)];

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = request.Workspace,
            Solution = SolutionIdentity.Create(request.Workspace, request.EntryPointPath),
            Name = discovered.Name,
            ProviderName = Name,
            Request = request,
        };

        foreach (string configuration in discovered.Configurations)
        {
            solution.Configurations.Add(configuration);
        }

        foreach (string platform in discovered.Platforms)
        {
            solution.Platforms.Add(platform);
        }

        foreach (DiscoveredProject listed in discovered.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (ProjectSnapshot? project, ProjectDiagnostic? failure) = await EvaluateAsync(
                listed.FullPath, listed.Name, request, knownProjects, cancellationToken).ConfigureAwait(false);

            // A project that will not evaluate stays in the snapshot carrying its diagnostic. It is
            // still a project the solution lists, a consumer still has to show it, and removing it
            // would leave the folder that holds it pointing at nothing.
            solution.Projects.Add(project ?? Unloadable(listed, request.Workspace, failure!));
        }

        foreach (SolutionFolder folder in Folders(discovered, request.Workspace))
        {
            solution.Folders.Add(folder);
        }

        return WorkspaceLoadResult.Success(solution.ToSnapshot());
    }

    private async ValueTask<(ProjectSnapshot? Project, ProjectDiagnostic? Failure)> EvaluateAsync(
        CanonicalPath path,
        string name,
        WorkspaceLoadRequest request,
        IReadOnlySet<CanonicalPath>? knownProjects,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Value))
        {
            return (null, Diagnostic(
                MSBuildDiagnosticCodes.ProjectFileNotFound,
                $"The solution lists '{name}' at '{path}', but there is no file there.",
                path));
        }

        Dictionary<string, string> globalProperties = GlobalProperties(request);
        bool includeItems = request.Options.IncludeItems;

        (EvaluatedProject? evaluated, string? error) =
            await RunAsync(path, globalProperties, includeItems, cancellationToken).ConfigureAwait(false);

        if (evaluated is null)
        {
            return (null, Diagnostic(
                MSBuildDiagnosticCodes.EvaluationFailed,
                $"'{path}' could not be evaluated: {error}",
                path));
        }

        ProjectSnapshot snapshot = Translate(evaluated, request, knownProjects);

        if (ChooseTargetFramework(snapshot, request) is not { } framework)
        {
            return (snapshot, null);
        }

        // A cross-targeting project's outer evaluation is not a build of anything: it exists to say
        // which frameworks there are. It has no TargetPath, so asking it where the output assembly
        // is gets no answer -- which is the question a designer most needs answered. Evaluating
        // again inside one framework produces a snapshot that belongs to an explicit context, which
        // is what the architecture asks for instead of guessing at a bin directory.
        globalProperties["TargetFramework"] = framework;

        (EvaluatedProject? inner, string? innerError) =
            await RunAsync(path, globalProperties, includeItems, cancellationToken).ConfigureAwait(false);

        return inner is null
            ? (null, Diagnostic(
                MSBuildDiagnosticCodes.EvaluationFailed,
                $"'{path}' could not be evaluated for '{framework}': {innerError}",
                path))
            : (Translate(inner, request, knownProjects), null);
    }

    /// <summary>
    /// Turns an evaluation into a snapshot, reading what restore resolved on the way.
    /// </summary>
    /// <remarks>
    /// The reading happens here rather than in the translator so that the translator stays a pure
    /// function over its input: this is the only part that touches a file.
    /// </remarks>
    private ProjectSnapshot Translate(
        EvaluatedProject evaluated,
        WorkspaceLoadRequest request,
        IReadOnlySet<CanonicalPath>? knownProjects)
    {
        var diagnostics = new List<ProjectDiagnostic>();
        ImmutableArray<ResolvedPackage> resolved = [];

        CanonicalPath assets = RestoreAssetsReader.Locate(evaluated);
        string? framework = evaluated.Properties.GetValueOrDefault("TargetFramework");

        if (!assets.IsEmpty && File.Exists(assets.Value))
        {
            if (!RestoreAssetsReader.TryRead(assets, framework, out resolved, out string? error))
            {
                diagnostics.Add(Diagnostic(
                    MSBuildDiagnosticCodes.RestoreAssetsMissing,
                    $"'{assets}' could not be read: {error}",
                    evaluated.FullPath,
                    ProjectDiagnosticSeverity.Warning));
            }
        }
        else if (DeclaresPackages(evaluated))
        {
            // Only worth saying when the project actually wants packages. A project with none is
            // not waiting for a restore, and telling it so would be noise in every solution.
            diagnostics.Add(Diagnostic(
                MSBuildDiagnosticCodes.RestoreAssetsMissing,
                $"'{evaluated.FullPath.FileName}' declares package references but has no restore output. " +
                "Run a restore to resolve them.",
                evaluated.FullPath,
                ProjectDiagnosticSeverity.Warning));
        }

        return MSBuildProjectTranslator.Translate(
            evaluated, request.Workspace, Name, knownProjects, resolved, diagnostics);
    }

    private static bool DeclaresPackages(EvaluatedProject project)
    {
        foreach (EvaluatedItem item in project.Items)
        {
            if (string.Equals(item.ItemType, MSBuildWellKnown.PackageReference, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The framework to evaluate inside, when the outer evaluation was not one.
    /// </summary>
    /// <remarks>
    /// Only when the project cross-targets and the caller did not choose. The first declared
    /// framework is taken, and the resulting snapshot says so in
    /// <see cref="ProjectSnapshot.ActiveTargetFramework"/> while still listing every framework in
    /// <see cref="ProjectSnapshot.TargetFrameworks"/> -- so a caller that cares which one it got can
    /// see it, and a caller that cares which one it wants passes
    /// <see cref="WorkspaceLoadRequest.TargetFramework"/>.
    /// </remarks>
    private static string? ChooseTargetFramework(ProjectSnapshot snapshot, WorkspaceLoadRequest request) =>
        request.TargetFramework is null
            && snapshot.TargetFrameworks.Length > 1
            && snapshot.ActiveTargetFramework is null
                ? snapshot.TargetFrameworks[0]
                : null;

    private static async ValueTask<(EvaluatedProject? Evaluated, string? Error)> RunAsync(
        CanonicalPath path,
        Dictionary<string, string> globalProperties,
        bool includeItems,
        CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string>(globalProperties, StringComparer.OrdinalIgnoreCase);

        (EvaluatedProject? evaluated, string? error) = await Task.Run(
            () => MSBuildProjectEvaluator.TryEvaluate(
                path, properties, includeItems, out EvaluatedProject? result, out string? failure)
                ? (result, (string?)null)
                : (null, failure),
            cancellationToken).ConfigureAwait(false);

        // A slow evaluation cannot be abandoned part-way, so this is where a cancellation that
        // arrived during it takes effect.
        cancellationToken.ThrowIfCancellationRequested();

        return (evaluated, error);
    }

    /// <summary>
    /// A snapshot for a project the solution lists and MSBuild could not read.
    /// </summary>
    /// <remarks>
    /// Carries what the solution knew — a name, a path, an identity — and the reason, and nothing
    /// else. The alternative, dropping it, would make a solution quietly smaller than it is.
    /// </remarks>
    private ProjectSnapshot Unloadable(
        DiscoveredProject listed,
        WorkspaceIdentity workspace,
        ProjectDiagnostic failure)
    {
        var builder = new ProjectSnapshotBuilder
        {
            Identity = ProjectIdentity.Create(workspace, listed.FullPath),
            Name = listed.Name,
            ProjectFilePath = listed.FullPath,
            ProviderName = Name,
        };

        builder.Diagnostics.Add(failure);

        return builder.ToSnapshot();
    }

    private static IEnumerable<CanonicalPath> Enumerate(DiscoveredSolution solution)
    {
        foreach (DiscoveredProject project in solution.Projects)
        {
            yield return project.FullPath;
        }
    }

    private static IEnumerable<SolutionFolder> Folders(DiscoveredSolution discovered, WorkspaceIdentity workspace)
    {
        foreach (DiscoveredFolder folder in discovered.Folders)
        {
            ImmutableArray<ProjectIdentity>.Builder members = ImmutableArray.CreateBuilder<ProjectIdentity>();

            foreach (DiscoveredProject project in discovered.Projects)
            {
                if (string.Equals(project.FolderPath, folder.Path, StringComparison.Ordinal))
                {
                    members.Add(ProjectIdentity.Create(workspace, project.FullPath));
                }
            }

            yield return new SolutionFolder
            {
                Name = folder.Name,
                Path = folder.Path,
                Projects = members.ToImmutable(),
            };
        }
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

    private static ProjectDiagnostic Diagnostic(
        string code,
        string message,
        CanonicalPath path,
        ProjectDiagnosticSeverity severity = ProjectDiagnosticSeverity.Error) =>
        ProjectDiagnostic.ForFile(code, message, severity, path) with
        {
            ProviderName = "MSBuild",
        };

    private static WorkspaceLoadResult Failure(string code, string message, CanonicalPath path) =>
        WorkspaceLoadResult.Failure(Diagnostic(code, message, path));
}

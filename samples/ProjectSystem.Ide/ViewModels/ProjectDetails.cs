using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using ArxisStudio.ProjectSystem;

namespace ProjectSystem.Ide.ViewModels;

/// <summary>One row of a name-and-value list.</summary>
public sealed record Fact(string Name, string Value);

/// <summary>A diagnostic, flattened for a grid.</summary>
public sealed record DiagnosticRow(string Severity, string Code, string Message, string Where, string Source)
{
    public static DiagnosticRow From(ProjectDiagnostic diagnostic) => new(
        diagnostic.Severity.ToString(),
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.FilePath.IsEmpty
            ? string.Empty
            : diagnostic.Span.IsEmpty
                ? diagnostic.FilePath.FileName
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"{diagnostic.FilePath.FileName}({diagnostic.Span.StartLine},{diagnostic.Span.StartColumn})"),
        diagnostic.ProviderName ?? string.Empty);
}

/// <summary>
/// Everything a project snapshot says, arranged for reading.
/// </summary>
/// <remarks>
/// Deliberately exhaustive. The point of the sample is that a consumer never has to reach past the
/// model for any of this — no MSBuild object is touched to fill in a single row below.
/// </remarks>
public sealed class ProjectDetails
{
    public required string Title { get; init; }

    public required ImmutableArray<Fact> Identity { get; init; }

    public required ImmutableArray<Fact> Context { get; init; }

    public required ImmutableArray<Fact> Properties { get; init; }

    public required ImmutableArray<Fact> References { get; init; }

    public required ImmutableArray<Fact> Packages { get; init; }

    public required ImmutableArray<Fact> Outputs { get; init; }

    public required ImmutableArray<Fact> Inputs { get; init; }

    public required ImmutableArray<Fact> RuntimeAssemblies { get; init; }

    public static ProjectDetails For(SolutionSnapshot snapshot, ProjectSnapshot project) => new()
    {
        Title = project.Name,

        Identity =
        [
            new Fact("Project file", project.ProjectFilePath.Value),
            new Fact("Directory", project.ProjectDirectory.Value),
            new Fact("Identity", project.Identity.ToString()),
            new Fact("Language", project.Language ?? "—"),
            new Fact("Kind", project.Kind ?? "—"),
            new Fact("Provider", project.ProviderName ?? "—"),
            new Fact("Has errors", project.HasErrors ? "yes" : "no"),
        ],

        Context =
        [
            new Fact("Active framework", project.ActiveTargetFramework ?? "—"),
            new Fact("Declared frameworks", Join(project.TargetFrameworks)),
            new Fact("Active configuration", project.ActiveConfiguration ?? "—"),
            new Fact("Configurations", Join(project.Configurations)),
            new Fact("Active platform", project.ActivePlatform ?? "—"),
            new Fact("Platforms", Join(project.Platforms)),
            new Fact("Solution configurations", Join(snapshot.Configurations)),
            new Fact("Solution platforms", Join(snapshot.Platforms)),
        ],

        Properties = [.. project.Properties
            .OrderBy(static p => p.Key, System.StringComparer.OrdinalIgnoreCase)
            .Select(static p => new Fact(p.Key, p.Value))],

        References =
        [
            .. project.ProjectReferences.Select(static r => new Fact(
                "Project",
                r.ProjectFilePath.FileName
                    + (r.Project.IsEmpty ? "  (outside the snapshot)" : string.Empty)
                    + (r.Aliases.IsDefaultOrEmpty ? string.Empty : "  aliases: " + string.Join(",", r.Aliases)))),

            .. project.FrameworkReferences.Select(static r => new Fact("Framework", r.Name)),

            .. project.AssemblyReferences.Select(static r => new Fact(
                "Assembly", r.Name + (r.HintPath.IsEmpty ? string.Empty : "  → " + r.HintPath.FileName))),

            .. project.AnalyzerReferences.Select(static r => new Fact("Analyzer", r.AssemblyPath.FileName)),
        ],

        Packages =
        [
            .. project.PackageReferences.Select(static p => new Fact(
                "Declared",
                p.PackageId + " " + (p.VersionText ?? "(centrally managed)")
                    + (p.PrivateAssets is null ? string.Empty : "  private: " + p.PrivateAssets))),

            .. project.ResolvedPackages.Select(static p => new Fact(
                p.IsDirect ? "Resolved" : "Resolved (transitive)",
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"{p.PackageId} {p.Version}  —  {p.CompileAssemblies.Length} compile, {p.RuntimeAssemblies.Length} runtime"))),
        ],

        Outputs = [.. project.Outputs.Select(static o => new Fact(
            o.Kind.ToString(), o.Path.Value + (o.TargetFramework is null ? string.Empty : $"  [{o.TargetFramework}]")))],

        Inputs = [.. project.EvaluationInputs.Select(static i => new Fact("Input", i.Value))],

        RuntimeAssemblies = [.. snapshot.GetRuntimeAssemblies(project.Identity)
            .Select(static a => new Fact(
                a.Origin.ToString() + (a.PackageId is null ? string.Empty : $" ({a.PackageId})"),
                a.Path.Value))],
    };

    private static string Join(IEnumerable<string> values)
    {
        string joined = string.Join(", ", values);

        return joined.Length == 0 ? "—" : joined;
    }
}

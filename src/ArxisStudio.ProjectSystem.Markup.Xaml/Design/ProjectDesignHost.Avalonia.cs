using System;
using System.Collections.Immutable;
using System.Globalization;
using Avalonia;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>Which Avalonia a design set is built against, measured against the one this process runs (ADR 0034).</summary>
public sealed partial class ProjectDesignHost
{
    private const string AvaloniaPackage = "Avalonia";

    // What the design set's Avalonia says against this process's: nothing, a difference worth saying, or a
    // reason no generation is made. Written with the generation's own diagnostics, read with them.
    private ImmutableArray<ProjectDiagnostic> _compatibility = [];
    private string? _unsupportedReason;

    /// <summary>
    /// Gets why no generation is made while the design set is built against another major version of
    /// Avalonia than this process runs, or <see langword="null"/> while it is not.
    /// </summary>
    public string? UnsupportedReason
    {
        get
        {
            lock (_sync)
            {
                return _unsupportedReason;
            }
        }
    }

    /// <summary>Gets the version of Avalonia this process runs.</summary>
    internal static Version HostAvalonia { get; } = typeof(AvaloniaObject).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// Measures the design set's Avalonia against the process's, records what it found, and answers whether
    /// a generation may be made of it.
    /// </summary>
    /// <remarks>
    /// A project built against another major version names members this process does not have: its compiled
    /// markup calls them, and they fail one by one, in whatever the designer happened to show. Nothing of it is
    /// loaded. Another minor version loads, and is said. A project restore has not answered for yet is not
    /// measured: what it will be built against is not known.
    /// </remarks>
    private bool AvaloniaFits(SolutionSnapshot snapshot, ImmutableArray<ProjectIdentity> set)
    {
        (ImmutableArray<ProjectDiagnostic> diagnostics, string? unsupported) = MeasureAvalonia(snapshot, set, HostAvalonia);

        lock (_sync)
        {
            _compatibility = diagnostics;
            _unsupportedReason = unsupported;
        }

        return unsupported is null;
    }

    /// <summary>What the Avalonia each project of a set resolved says against a host's.</summary>
    internal static (ImmutableArray<ProjectDiagnostic> Diagnostics, string? Unsupported) MeasureAvalonia(
        SolutionSnapshot snapshot,
        ImmutableArray<ProjectIdentity> set,
        Version host)
    {
        ImmutableArray<ProjectDiagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<ProjectDiagnostic>();
        string? unsupported = null;

        foreach (ProjectIdentity identity in set)
        {
            if (!snapshot.TryGetProject(identity, out ProjectSnapshot? project)
                || ResolvedAvalonia(project) is not { } resolved
                || ParseVersion(resolved.Version) is not { } version)
            {
                continue;
            }

            if (version.Major != host.Major)
            {
                string message = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is built against Avalonia {1}, and the designer runs Avalonia {2}. Its forms cannot be shown "
                        + "in this designer; their markup can still be read.",
                    project.Name,
                    resolved.Version,
                    Short(host));

                diagnostics.Add(new ProjectDiagnostic(ProjectDesignDiagnosticCodes.AvaloniaVersionUnsupported, message, ProjectDiagnosticSeverity.Error));
                unsupported ??= message;
            }
            else if (version.Minor != host.Minor)
            {
                diagnostics.Add(new ProjectDiagnostic(
                    ProjectDesignDiagnosticCodes.AvaloniaVersionDiffers,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} is built against Avalonia {1}, and the designer runs Avalonia {2}. Its forms are shown, "
                            + "and may differ from what the program shows.",
                        project.Name,
                        resolved.Version,
                        Short(host)),
                    ProjectDiagnosticSeverity.Warning));
            }
        }

        return (diagnostics.ToImmutable(), unsupported);
    }

    private static ResolvedPackage? ResolvedAvalonia(ProjectSnapshot project)
    {
        foreach (ResolvedPackage package in project.ResolvedPackages)
        {
            if (string.Equals(package.PackageId, AvaloniaPackage, StringComparison.OrdinalIgnoreCase))
            {
                return package;
            }
        }

        return null;
    }

    /// <summary>The numeric part of a package version: <c>12.1.0-beta1</c> is 12.1.0.</summary>
    private static Version? ParseVersion(string text)
    {
        int end = text.IndexOfAny(['-', '+']);

        return Version.TryParse(end < 0 ? text : text[..end], out Version? version) ? version : null;
    }

    private static string Short(Version version) =>
        version.Build >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}")
            : string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}");
}

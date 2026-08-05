using System;
using System.Collections.Generic;
using System.Linq;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// The dependency rules the shipping packages may not break, in one place so that the declarative
/// check and the compiled check cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// There are two different questions here and they have different answers, which is why there are
/// two methods. <b>What a package may reference</b> depends on the package: each exists to host one
/// engine, so the MSBuild provider references MSBuild and the package manager references NuGet,
/// while the core references neither and nothing references both.
/// <b>What may appear in a public surface</b> does not depend on the package: no consumer of any
/// package in this family should need MSBuild or NuGet on their compile line to inspect a snapshot.
/// </para>
/// <para>
/// Conflating the two would either forbid the provider from doing its job or let engine types leak
/// through it, and the second is the failure the whole boundary exists to prevent.
/// </para>
/// </remarks>
internal static class ForbiddenDependencies
{
    /// <summary>Out of scope by default, for every shipping package.</summary>
    private static readonly string[] Engines =
    [
        "Microsoft.Build",
        "MSBuild",
        "NuGet.",
        "Microsoft.CodeAnalysis",
        "Avalonia",
        "ArxisStudio.Markup",
        "System.Windows",
        "Microsoft.WindowsDesktop",
        "Microsoft.Maui",
        "Uno.",
        "WinUI",
        "Gtk",
    ];

    /// <summary>
    /// The one engine each package exists to host, and nothing else.
    /// </summary>
    /// <remarks>
    /// Stated per package rather than as "everything except the core", because the interesting rule
    /// is not that the core is special — it is that a package hosting one engine must not quietly
    /// acquire a second. The MSBuild provider evaluating packages, or the package manager evaluating
    /// projects, would each put two readers of the same files in one repository.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Hosts = new(StringComparer.Ordinal)
    {
        [RepositoryLayout.MSBuildPackage] = ["Microsoft.Build", "MSBuild"],
        [RepositoryLayout.NuGetPackage] = ["NuGet."],
    };

    /// <summary>Whether a package may not reference something.</summary>
    /// <param name="package">The shipping package doing the referencing.</param>
    /// <param name="identifier">The package id or assembly name being referenced.</param>
    /// <returns><see langword="true"/> when the reference breaks the boundary.</returns>
    public static bool IsForbiddenReference(string package, string identifier)
    {
        string[] hosted = Hosts.GetValueOrDefault(package, []);

        return Engines
            .Where(prefix => !hosted.Contains(prefix, StringComparer.Ordinal))
            .Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether something may not appear in any package's public surface.
    /// </summary>
    /// <remarks>
    /// Stricter than <see cref="IsForbiddenReference"/> on purpose: the MSBuild provider references
    /// MSBuild and must still not hand a <c>ProjectInstance</c> to anybody. That is the rule that
    /// lets a snapshot outlive the engine that produced it.
    /// </remarks>
    /// <param name="identifier">The assembly name of a type appearing in a public member.</param>
    /// <returns><see langword="true"/> when it may not be exposed.</returns>
    public static bool IsForbiddenInPublicApi(string identifier) =>
        Engines.Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal));
}

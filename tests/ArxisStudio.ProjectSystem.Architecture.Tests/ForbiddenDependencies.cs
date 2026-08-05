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

        // The adapter is the exception the rule was written to make possible. Every other package
        // is kept away from Markup and Avalonia precisely so that one package can join them
        // deliberately, in the open, where the dependency is the point rather than an accident.
        [RepositoryLayout.AdapterPackage] = ["ArxisStudio.Markup", "Avalonia"],
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
    /// Whether something may not appear in a package's public surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stricter than <see cref="IsForbiddenReference"/> for the packages that read and write
    /// projects: the MSBuild provider references MSBuild and must still not hand a
    /// <c>ProjectInstance</c> to anybody, and the package manager must not hand out a
    /// <c>NuGetVersion</c>. That is the rule that lets a snapshot outlive the engine that produced
    /// it.
    /// </para>
    /// <para>
    /// The adapter is the one place where it does not apply, because handing out Markup's types is
    /// the entire reason it exists — a consumer asking it for a <c>XamlLoadEnvironment</c> is
    /// already holding Markup. It is still kept away from MSBuild and NuGet: adapting the model to
    /// Markup needs neither, and a consumer of the adapter should not acquire them.
    /// </para>
    /// </remarks>
    /// <param name="package">The shipping package whose surface is being inspected.</param>
    /// <param name="identifier">The assembly name of a type appearing in a public member.</param>
    /// <returns><see langword="true"/> when it may not be exposed.</returns>
    public static bool IsForbiddenInPublicApi(string package, string identifier)
    {
        string[] hosted = string.Equals(package, RepositoryLayout.AdapterPackage, StringComparison.Ordinal)
            ? Hosts[RepositoryLayout.AdapterPackage]
            : [];

        return Engines
            .Where(prefix => !hosted.Contains(prefix, StringComparer.Ordinal))
            .Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal));
    }
}

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
/// two methods. <b>What a package may reference</b> depends on the package: the MSBuild provider
/// exists to host MSBuild, so it references it, while the core references nothing.
/// <b>What may appear in a public surface</b> does not depend on the package: no consumer of any
/// package in this family should need MSBuild on their compile line to inspect a snapshot.
/// </para>
/// <para>
/// Conflating the two would either forbid the provider from doing its job or let engine types leak
/// through it, and the second is the failure the whole boundary exists to prevent.
/// </para>
/// </remarks>
internal static class ForbiddenDependencies
{
    /// <summary>Out of scope for every shipping package, whatever its job.</summary>
    private static readonly string[] Everywhere =
    [
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

    /// <summary>Additionally out of scope for the provider-neutral core.</summary>
    private static readonly string[] CoreOnly =
    [
        "Microsoft.Build",
        "MSBuild",
    ];

    /// <summary>Whether a package may not reference something.</summary>
    /// <param name="package">The shipping package doing the referencing.</param>
    /// <param name="identifier">The package id or assembly name being referenced.</param>
    /// <returns><see langword="true"/> when the reference breaks the boundary.</returns>
    public static bool IsForbiddenReference(string package, string identifier)
    {
        IEnumerable<string> prefixes = string.Equals(package, RepositoryLayout.CorePackage, StringComparison.Ordinal)
            ? Everywhere.Concat(CoreOnly)
            : Everywhere;

        return prefixes.Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal));
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
        Everywhere.Concat(CoreOnly).Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal));
}

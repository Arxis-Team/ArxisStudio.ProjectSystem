using System;
using System.Collections.Generic;
using System.Linq;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// The dependency rules the core package may not break, in one place so that the declarative
/// check and the compiled check cannot drift apart.
/// </summary>
/// <remarks>
/// These are the task specification's independence rules: the core is provider-neutral, so no
/// build engine, package client, compiler, UI framework or sibling library may reach it. A
/// provider package implements the boundary and depends on the core, never the other way round.
/// </remarks>
internal static class ForbiddenDependencies
{
    /// <summary>Identifier prefixes that signal out-of-scope functionality creeping in.</summary>
    public static IReadOnlyList<string> Prefixes { get; } =
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
    /// Build-time-only analyzers that ship no runtime code, so they cannot bring the forbidden
    /// capability into the package.
    /// </summary>
    public static IReadOnlyList<string> Allowed { get; } =
    [
        "Microsoft.CodeAnalysis.PublicApiAnalyzers",
    ];

    /// <summary>Whether an identifier names something the core may not depend on.</summary>
    public static bool IsForbidden(string identifier) =>
        !Allowed.Contains(identifier, StringComparer.Ordinal)
        && Prefixes.Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal));
}

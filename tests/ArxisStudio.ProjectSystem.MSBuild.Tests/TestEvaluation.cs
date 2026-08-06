using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// Builds evaluation inputs by hand, so a mapping assertion costs a microsecond rather than an SDK.
/// </summary>
internal static class TestEvaluation
{
    internal static WorkspaceIdentity Workspace { get; } = WorkspaceIdentity.New();

    /// <summary>Gets the platform's root, so these tests run on Linux as well as Windows.</summary>
    internal static string Root => OperatingSystem.IsWindows() ? "C:\\" : "/";

    internal static string Native(params string[] segments) =>
        Root + string.Join(Path.DirectorySeparatorChar, segments);

    internal static CanonicalPath ProjectPath(string name = "App", string extension = ".csproj") =>
        CanonicalPath.Create(Native("src", name, name + extension));

    internal static ProjectMetadata Meta(params (string Key, string Value)[] pairs) =>
        ProjectMetadata.Create(pairs.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));

    /// <summary>An evaluated project with the properties given and nothing else.</summary>
    internal static EvaluatedProject Project(
        CanonicalPath? path = null,
        ProjectMetadata? properties = null,
        IEnumerable<CanonicalPath>? imports = null,
        params EvaluatedItem[] items) =>
        new()
        {
            FullPath = path ?? ProjectPath(),
            Properties = properties ?? ProjectMetadata.Empty,
            Imports = imports is null ? [] : [.. imports],
            Items = [.. items],
        };

    internal static EvaluatedItem Item(
        string itemType,
        string include,
        CanonicalPath fullPath = default,
        ProjectMetadata? metadata = null,
        bool imported = false) =>
        new()
        {
            ItemType = itemType,
            EvaluatedInclude = include,
            FullPath = fullPath,
            Metadata = metadata ?? ProjectMetadata.Empty,
            IsImported = imported,
        };

    internal static ProjectSnapshot Translate(EvaluatedProject project, CanonicalPath ceiling = default) =>
        MSBuildProjectTranslator.Translate(project, Workspace, "MSBuild", ceiling: ceiling);

    /// <summary>
    /// The convention-based files a project names because MSBuild would look for them there.
    /// </summary>
    /// <remarks>
    /// Every project has these among its evaluation inputs, so the tests that are about something
    /// else say so by name rather than by listing them again.
    /// </remarks>
    internal static IEnumerable<CanonicalPath> WalkedIn(params string[] directorySegments) =>
        WalkedNames.Select(name => CanonicalPath.Create(Native([.. directorySegments, name])));

    private static readonly string[] WalkedNames =
        ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];
}

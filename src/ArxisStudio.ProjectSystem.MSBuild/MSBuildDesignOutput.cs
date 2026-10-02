using System.Collections.Generic;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Where a designer's builds go: beside the IDE's, never over them.
/// </summary>
/// <remarks>
/// <para>
/// A designer builds the projects it shows, and the IDE beside it builds the same ones into the same
/// folders by default. The application the IDE started then locks the output the designer's build has
/// to write (<c>MSB3027</c>), and two builds started together write one intermediate folder. Pass
/// <see cref="GlobalProperties"/> to the designer's loads and operations alike —
/// <see cref="WorkspaceLoadRequest.GlobalProperties"/> and <see cref="ProjectOperationRequest.GlobalProperties"/> —
/// and its builds land in <c>bin/ArxisStudio/</c> and <c>obj/ArxisStudio/</c> of each project, where
/// the evaluation says they will
/// (<see href="../../docs/adr/0026-a-design-build-writes-beside-the-ides-never-over-it.md">ADR 0026</see>).
/// </para>
/// <para>
/// <b>The output paths are set, the bases are not.</b> The SDK's default excludes come from
/// <c>BaseOutputPath</c> and <c>BaseIntermediateOutputPath</c>; moving those would make the IDE's
/// output items of the project. With the bases where they are, neither tool's output is globbed, and
/// the restore — <c>obj/project.assets.json</c> — is the one both read.
/// </para>
/// <para>
/// <b>One framework per design build.</b> A global output path is not given the framework folder the
/// SDK appends otherwise, so a multi-targeted project's design build names one framework through the
/// request's <c>TargetFramework</c>. Referenced projects receive the same properties and build into
/// their own design folders.
/// </para>
/// </remarks>
public static class MSBuildDesignOutput
{
    /// <summary>The folder below each project's <c>bin</c> and <c>obj</c> that design builds write to.</summary>
    public const string FolderName = "ArxisStudio";

    /// <summary>
    /// Gets the global properties that send a build's output and intermediate files to the design
    /// folders: <c>OutputPath=bin/ArxisStudio/</c> and <c>IntermediateOutputPath=obj/ArxisStudio/</c>.
    /// </summary>
    /// <remarks>
    /// Relative, so every project — the one built and each it references — resolves them against
    /// itself.
    /// </remarks>
    public static ProjectMetadata GlobalProperties { get; } = ProjectMetadata.Create(
    [
        new KeyValuePair<string, string>("OutputPath", "bin/" + FolderName + "/"),
        new KeyValuePair<string, string>("IntermediateOutputPath", "obj/" + FolderName + "/"),
    ]);
}

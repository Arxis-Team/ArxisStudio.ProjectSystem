using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// What an evaluation produced, in a shape that owes nothing to MSBuild.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that makes the translation testable. If the translator took
/// <c>Microsoft.Build.Evaluation.Project</c>, then testing it would mean evaluating a real project,
/// and every assertion about mapping a property to a snapshot field would carry an SDK, a
/// filesystem and several seconds with it. Almost every bug in this package will be in the mapping,
/// so the mapping is the part that must be cheap and exhaustive to test.
/// </para>
/// <para>
/// So the adapter over MSBuild is deliberately thin and dull — it copies fields — and the
/// translation is a pure function over this. The adapter is covered by a handful of integration
/// tests against fixture projects; the translation is covered properly.
/// </para>
/// </remarks>
internal sealed record EvaluatedProject
{
    /// <summary>Gets the canonical path of the project file.</summary>
    public required CanonicalPath FullPath { get; init; }

    /// <summary>Gets the evaluated properties worth surfacing.</summary>
    public ProjectMetadata Properties { get; init; } = ProjectMetadata.Empty;

    /// <summary>Gets the evaluated items.</summary>
    public ImmutableArray<EvaluatedItem> Items
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Gets what the engine said while evaluating.</summary>
    public ImmutableArray<EngineMessage> Messages
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];
}

/// <summary>
/// Something the engine reported, in a shape that owes nothing to MSBuild.
/// </summary>
/// <remarks>
/// An unresolvable SDK, a missing import, a workload the machine does not have, a compiler error:
/// MSBuild notices all of these and names them with codes of its own that consumers already know.
/// Those names are kept rather than replaced, because a code the caller can act on is worth more
/// than a code that merely belongs to this library's range. Used for evaluation and for builds,
/// which report through the same mechanism.
/// </remarks>
internal sealed record EngineMessage
{
    /// <summary>Gets the engine's own code, such as <c>MSB4236</c> or <c>NETSDK1147</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the engine's message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets a value indicating whether the engine called it an error rather than a warning.</summary>
    public required bool IsError { get; init; }

    /// <summary>Gets the file it concerns, when the engine named one.</summary>
    public CanonicalPath File { get; init; }

    /// <summary>Gets the one-based line, or zero.</summary>
    public int Line { get; init; }

    /// <summary>Gets the one-based column, or zero.</summary>
    public int Column { get; init; }
}

/// <summary>One evaluated item, in the same provider-neutral shape.</summary>
internal sealed record EvaluatedItem
{
    /// <summary>Gets the MSBuild item type, such as <c>Compile</c> or <c>PackageReference</c>.</summary>
    public required string ItemType { get; init; }

    /// <summary>Gets the include as evaluated, with properties and globs already expanded.</summary>
    public required string EvaluatedInclude { get; init; }

    /// <summary>Gets the resolved location, or <see cref="CanonicalPath.None"/> when the item names no file.</summary>
    public CanonicalPath FullPath { get; init; }

    /// <summary>Gets the item's metadata.</summary>
    public ProjectMetadata Metadata { get; init; } = ProjectMetadata.Empty;

    /// <summary>Gets a value indicating whether the item came from an import rather than the project file.</summary>
    public bool IsImported { get; init; }
}

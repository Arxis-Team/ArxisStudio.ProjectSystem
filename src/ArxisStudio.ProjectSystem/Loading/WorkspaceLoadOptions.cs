namespace ArxisStudio.ProjectSystem;

/// <summary>
/// What a caller wants a load to include.
/// </summary>
/// <remarks>
/// <para>
/// A record rather than a <c>[Flags]</c> enum: adding a property later is not a breaking change,
/// and <c>Options with { IncludeItems = false }</c> reads better at a call site than a bitwise
/// expression.
/// </para>
/// <para>
/// These are <b>permissions, not obligations</b>. An option set to <see langword="false"/> tells a
/// provider it may skip expensive work; a provider that cannot skip it must still produce a
/// correct snapshot. Nothing here is a promise to a consumer that a collection will be empty.
/// </para>
/// </remarks>
public sealed record WorkspaceLoadOptions
{
    /// <summary>Gets the default options, which include everything.</summary>
    public static WorkspaceLoadOptions Default { get; } = new();

    /// <summary>Gets a value indicating whether project items are wanted. Defaults to <see langword="true"/>.</summary>
    public bool IncludeItems { get; init; } = true;

    /// <summary>Gets a value indicating whether build output artifacts are wanted. Defaults to <see langword="true"/>.</summary>
    public bool IncludeOutputArtifacts { get; init; } = true;
}

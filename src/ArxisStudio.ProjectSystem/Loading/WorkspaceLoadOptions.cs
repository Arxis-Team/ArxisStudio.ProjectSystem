using System;
using System.Collections.Immutable;
using System.Linq;

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

    /// <summary>
    /// Gets the names of evaluated properties to surface on <see cref="ProjectSnapshot.Properties"/>
    /// beyond the ones a provider surfaces anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a consumer whose question is a property nobody else asks — a designer asking whether a
    /// project compiles its bindings by default (<c>AvaloniaUseCompiledBindingsByDefault</c>) — so that
    /// the answer comes from the evaluation the workspace already ran, rather than from a second one
    /// or from reading the project file by hand. A provider that has no such property surfaces
    /// nothing for it.
    /// </para>
    /// <para>
    /// Names compare case-insensitively, as MSBuild property names do. Empty by default.
    /// </para>
    /// </remarks>
    public ImmutableArray<string> AdditionalProperties
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Determines whether two sets of options ask for the same load.</summary>
    /// <remarks>
    /// By the names in <see cref="AdditionalProperties"/>, not by the array that holds them: two
    /// options built separately with the same names ask for the same thing.
    /// </remarks>
    /// <param name="other">The options to compare with.</param>
    /// <returns><see langword="true"/> when they ask for the same load.</returns>
    public bool Equals(WorkspaceLoadOptions? other) =>
        other is not null
        && IncludeItems == other.IncludeItems
        && AdditionalProperties.SequenceEqual(other.AdditionalProperties, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns a hash code consistent with <see cref="Equals(WorkspaceLoadOptions?)"/>.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(IncludeItems);

        foreach (string name in AdditionalProperties)
        {
            hash.Add(name, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }
}

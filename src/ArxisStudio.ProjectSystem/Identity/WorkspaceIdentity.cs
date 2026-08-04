using System;
using System.Globalization;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Identifies one opened workspace.
/// </summary>
/// <remarks>
/// <para>
/// Minted when a workspace is created and carried by every identity that workspace hands out. That
/// is what stops two workspaces over the same solution from producing interchangeable project
/// identities — a consumer caching state by identity would otherwise show one workspace's data
/// under the other's key.
/// </para>
/// <para>
/// A workspace identity is <b>not stable across process launches</b> and must not be persisted.
/// It is a handle for the lifetime of the object that minted it.
/// </para>
/// </remarks>
/// <param name="Value">The underlying value.</param>
public readonly record struct WorkspaceIdentity(Guid Value)
{
    /// <summary>Gets the absent identity, which is also <see langword="default"/>.</summary>
    public static WorkspaceIdentity None => default;

    /// <summary>Gets a value indicating whether this is <see cref="None"/>.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new, unique workspace identity.</summary>
    /// <returns>The new identity.</returns>
    public static WorkspaceIdentity New() => new(Guid.NewGuid());

    /// <summary>Returns the identity in a readable form.</summary>
    /// <returns>The underlying value, or <c>(none)</c> when absent.</returns>
    public override string ToString() =>
        IsEmpty ? "(none)" : Value.ToString("D", CultureInfo.InvariantCulture);
}

using System;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A request to open something, in terms no provider-specific type appears in.
/// </summary>
/// <remarks>
/// <para>
/// A record, so that reloading under a different configuration is
/// <c>request with { Configuration = "Release" }</c> rather than a second constructor call that
/// has to repeat everything else.
/// </para>
/// <para>
/// <see cref="Workspace"/> is <c>required</c> rather than defaulting to
/// <see cref="WorkspaceIdentity.None"/>, because a provider mints project identities from it. A
/// silent default would make identities minted by unrelated requests compare equal, which is the
/// one thing project identity exists to prevent. The workspace overwrites this with its own
/// identity before calling any provider, and then checks that what came back carries it.
/// </para>
/// </remarks>
public sealed record WorkspaceLoadRequest
{
    /// <summary>Gets the workspace this load belongs to.</summary>
    public required WorkspaceIdentity Workspace
    {
        get => field;
        init => field = value.IsEmpty
            ? throw new ArgumentException(
                "A load request needs the identity of the workspace it belongs to; a provider mints " +
                "project identities from it.",
                nameof(value))
            : value;
    }

    /// <summary>Gets the canonical path of the file to open.</summary>
    public required CanonicalPath EntryPointPath
    {
        get => field;
        init => field = value.IsEmpty
            ? throw new ArgumentException("A load request needs something to open.", nameof(value))
            : value;
    }

    /// <summary>Gets the entry point, classified from <see cref="EntryPointPath"/>.</summary>
    public WorkspaceEntryPoint EntryPoint => WorkspaceEntryPoint.FromPath(EntryPointPath);

    /// <summary>Gets the configuration to evaluate under, or <see langword="null"/> for the provider's default.</summary>
    public string? Configuration { get; init; }

    /// <summary>Gets the platform to evaluate under, or <see langword="null"/> for the provider's default.</summary>
    public string? Platform { get; init; }

    /// <summary>Gets the target framework to evaluate under, or <see langword="null"/> for the provider's default.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Gets properties a provider should evaluate with. Keys compare case-insensitively.</summary>
    public ProjectMetadata GlobalProperties { get; init; } = ProjectMetadata.Empty;

    /// <summary>Gets what the load should include.</summary>
    public WorkspaceLoadOptions Options { get; init; } = WorkspaceLoadOptions.Default;

    /// <summary>Returns the entry point and the configuration, when one was asked for.</summary>
    /// <returns>A readable representation of the request.</returns>
    public override string ToString() =>
        Configuration is null ? EntryPointPath.ToString() : $"{EntryPointPath} ({Configuration})";
}

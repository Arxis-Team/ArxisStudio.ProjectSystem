namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Something an operation has got to, reported while it runs.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. A build engine can emit thousands of messages a second, and a model that
/// tried to represent all of them would be a logging framework wearing a project system's name.
/// What a host needs to keep a progress bar honest is what is happening and to which project;
/// anything finer belongs in the engine's own log.
/// </para>
/// <para>
/// There is no percentage, because nothing can honestly supply one: a build's total work is not
/// known until it is done. A host that wants a bar should count projects, which it can.
/// </para>
/// </remarks>
public sealed record ProjectOperationProgress
{
    /// <summary>Gets a human-readable description of what is happening.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the project concerned, or <see cref="ProjectIdentity.None"/> when it is the whole operation.</summary>
    public ProjectIdentity Project { get; init; }

    /// <summary>Returns the message, prefixed by the project when there is one.</summary>
    /// <returns>A readable representation of the progress report.</returns>
    public override string ToString() => Project.IsEmpty ? Message : $"{Project}: {Message}";
}

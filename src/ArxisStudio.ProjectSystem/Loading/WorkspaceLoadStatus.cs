namespace ArxisStudio.ProjectSystem;

/// <summary>
/// How a load ended.
/// </summary>
/// <remarks>
/// Cancellation is deliberately not a member. A cancelled load throws
/// <see cref="System.OperationCanceledException"/>, so these three values are genuinely
/// exhaustive rather than three-plus-a-special-case.
/// </remarks>
public enum WorkspaceLoadStatus
{
    /// <summary>A snapshot was produced and nothing went wrong.</summary>
    Succeeded = 0,

    /// <summary>A snapshot was produced, but something in it failed. The snapshot is usable and incomplete.</summary>
    SucceededWithErrors = 1,

    /// <summary>No usable snapshot was produced.</summary>
    Failed = 2,
}

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>Where a <see cref="ProjectDesignHost"/> is in its life.</summary>
public enum ProjectDesignState
{
    /// <summary>Not started, or starting: the first generation is not live yet.</summary>
    Starting = 0,

    /// <summary>The generation is live and current; documents show it.</summary>
    Live = 1,

    /// <summary>A design build is running. The generation stays live while it does.</summary>
    Building = 2,

    /// <summary>
    /// The generation is stale and will be replaced as soon as nothing holds the swap off — a gesture,
    /// an edit being typed, a dialog (<see cref="ProjectDesignHost.Gate"/>) — and no build is pending.
    /// </summary>
    SwapPending = 3,

    /// <summary>The generation is being replaced. Opening a document waits for the successor.</summary>
    Swapping = 4,

    /// <summary>
    /// The generation could not be replaced in this process: only a new process shows the project's
    /// types as they are now (<see cref="ProjectDesignHost.RestartRequired"/>).
    /// </summary>
    RestartRequired = 5,

    /// <summary>The host was disposed.</summary>
    Disposed = 6,

    /// <summary>
    /// The design set is built against another major version of Avalonia than this process runs, and no
    /// generation is made of it (<see cref="ProjectDesignHost.UnsupportedReason"/>). Documents open with their
    /// text and nothing built from it. A snapshot naming a version the process runs lifts it.
    /// </summary>
    Unsupported = 7,
}

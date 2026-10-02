using ArxisStudio.Markup.Xaml.Loader;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>How <see cref="ProjectDesignHost.OpenDocumentAsync"/> opens a document.</summary>
public sealed record ProjectDesignDocumentOptions
{
    /// <summary>Gets the defaults: the file's text, and nothing lending the root.</summary>
    public static ProjectDesignDocumentOptions Default { get; } = new();

    /// <summary>
    /// Gets what lends the root back to the session while it writes to it — the designer's form
    /// container, which borrows a window's content to show it — or <see langword="null"/> when nothing
    /// borrows it.
    /// </summary>
    public IXamlRootAccess? RootAccess { get; init; }

    /// <summary>
    /// Gets what the document says, when it is restored rather than read — a session handed over by the
    /// previous copy of the designer, with edits nobody saved — or <see langword="null"/> to read the file.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets what the file held when <see cref="Text"/> was taken, so that the document reads as changed
    /// against it, or <see langword="null"/> for what the file holds now.
    /// </summary>
    /// <remarks>
    /// The file may have moved on since: the other editor goes on working while the designer restarts.
    /// <see cref="ProjectDesignHost.ReloadAsync"/> after opening says so — a conflict when the restored
    /// text has edits of its own, never a silent overwrite either way.
    /// </remarks>
    public string? SavedText { get; init; }
}

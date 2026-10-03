using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml.Loader;
using Avalonia;
using Avalonia.Styling;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// What a form's application declares — its styles, resources and data templates — loaded for that form
/// alone (ADR 0033).
/// </summary>
/// <remarks>
/// <para>
/// A form looks in the program the way its application dresses it: the theme <c>App.axaml</c> sets in
/// <c>Application.Styles</c>, the resources it declares. A designer whose own application is dressed
/// differently shows the form undressed unless it lends the form what the program's application says.
/// <see cref="Application"/> is that: a plain <see cref="Avalonia.Application"/> built from the document as
/// written — the program's own <c>App</c> class is never constructed, because that would be the program
/// starting inside the designer.
/// </para>
/// <para>
/// <b>Taken over, not shared.</b> Avalonia gives a style and a resource dictionary one owner, so a form
/// takes the application's styles, dictionary and data templates over — moves them out of
/// <see cref="Application"/> — and the next form opens an application of its own.
/// </para>
/// <para>
/// <b>Of one generation.</b> What the document declares may be of the project's types. Let go of it when
/// the host asks the designer to let go for a swap; the host disposes every application still open once
/// the participants have let go, and the successor is answered by opening again. A saved application
/// document is <see cref="ProjectDesignHost.ApplicationChanged"/>, and is answered the same way.
/// </para>
/// <para>
/// Read <see cref="Application"/> on the user interface thread: its objects are Avalonia's.
/// </para>
/// </remarks>
public sealed class ProjectDesignApplication : IAsyncDisposable
{
    private readonly ProjectDesignHost _host;

    // Dropped on closing rather than kept: a designer still holding this object after a swap would
    // otherwise hold the generation through it, and the swap would end in a restart.
    private XamlLoadSession? _session;
    private int _disposed;

    internal ProjectDesignApplication(
        ProjectDesignHost host,
        XamlLoadSession? session,
        CanonicalPath file,
        ProjectIdentity project,
        ImmutableArray<MarkupDiagnostic> diagnostics)
    {
        _host = host;
        _session = session;
        File = file;
        Project = project;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the application's document.</summary>
    public CanonicalPath File { get; }

    /// <summary>Gets the project whose application it is — the form's own, or one that references the form's project.</summary>
    public ProjectIdentity Project { get; }

    /// <summary>
    /// Gets the application as its document writes it, or <see langword="null"/> when the document produced
    /// nothing — <see cref="Diagnostics"/> says why — and once it is closed.
    /// </summary>
    public Application? Application => Volatile.Read(ref _session)?.RootObject as Application;

    /// <summary>Gets the theme variant the application asks for; <see cref="ThemeVariant.Default"/> when it asks for none.</summary>
    public ThemeVariant RequestedThemeVariant => Application?.RequestedThemeVariant ?? ThemeVariant.Default;

    /// <summary>Gets what loading the document noticed.</summary>
    public ImmutableArray<MarkupDiagnostic> Diagnostics { get; }

    /// <summary>Gets a value indicating whether the application has been closed — by its owner, or by a swap.</summary>
    public bool IsClosed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Closes the application's session and lets go of it. What a form took over of it stays the form's — and
    /// holds the generation until the form lets go of it too.
    /// </summary>
    /// <returns>A task that completes once the session is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _host.Forget(this);

        if (Interlocked.Exchange(ref _session, null) is { } session)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Raised when an application document a form's application was loaded from changed.</summary>
public sealed class ProjectDesignApplicationEventArgs : EventArgs
{
    internal ProjectDesignApplicationEventArgs(CanonicalPath file, ProjectIdentity project)
    {
        File = file;
        Project = project;
    }

    /// <summary>Gets the application's document.</summary>
    public CanonicalPath File { get; }

    /// <summary>Gets the project whose application it is.</summary>
    public ProjectIdentity Project { get; }
}

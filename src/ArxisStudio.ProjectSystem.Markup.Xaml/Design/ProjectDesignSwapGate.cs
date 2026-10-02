using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// What holds a swap of the project's types off: whatever in the designer is in the middle of
/// something a swap would cut.
/// </summary>
/// <remarks>
/// <para>
/// A swap takes every root off the canvas and builds it again, so it must not land on a gesture that
/// holds the pointer, an inspector value half typed, an open dialog, a drag, an edit in flight. Each of
/// those defers it for as long as it lasts — <see cref="Defer"/>, disposed when done — and the swap
/// runs the moment the last of them lets go: no polling, no waiting for the window to be in front.
/// </para>
/// <para>
/// Deferring is the only way to hold a swap off. The application running from the designer does not,
/// nor does the window being in the background: the types replaced are the designer's, not theirs.
/// </para>
/// </remarks>
public sealed class ProjectDesignSwapGate
{
    private readonly Lock _sync = new();
    private readonly List<Deferral> _deferrals = [];

    internal ProjectDesignSwapGate()
    {
    }

    /// <summary>Raised when a deferral is taken or let go, on the thread that did it.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets a value indicating whether nothing holds a swap off.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _deferrals.Count == 0;
            }
        }
    }

    /// <summary>Gets what holds a swap off now, oldest first.</summary>
    public ImmutableArray<string> Reasons
    {
        get
        {
            lock (_sync)
            {
                return [.. _deferrals.Select(static deferral => deferral.Reason)];
            }
        }
    }

    /// <summary>Holds a swap off until the returned object is disposed.</summary>
    /// <param name="reason">What is in progress, for anybody asking why the types are not new yet.</param>
    /// <returns>The deferral. Disposing it twice lets go once.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <see langword="null"/>, empty or blank.</exception>
    public IDisposable Defer(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var deferral = new Deferral(this, reason);

        lock (_sync)
        {
            _deferrals.Add(deferral);
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return deferral;
    }

    private void Release(Deferral deferral)
    {
        bool removed;

        lock (_sync)
        {
            removed = _deferrals.Remove(deferral);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Deferral(ProjectDesignSwapGate gate, string reason) : IDisposable
    {
        public string Reason => reason;

        public void Dispose() => gate.Release(this);
    }
}
